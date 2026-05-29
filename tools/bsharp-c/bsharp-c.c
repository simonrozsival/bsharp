/*
 * bsharp-c — plain-C launcher prototype for bsharp.
 *
 * Same scope as tools/bsharp-go: warm fast-path only (arg parse + cache-root
 * resolution + freshness check + execve into the host). Anything else
 * delegates to the sibling C# launcher (`bsharp`). This file deliberately
 * uses only libc + CommonCrypto (already in libSystem on macOS, no extra
 * link cost) so startup is essentially free.
 *
 * Build: cc -O2 -o bsharp-c bsharp-c.c
 */

#include <ctype.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

#include <CommonCrypto/CommonDigest.h>

#ifndef PATH_MAX
#define PATH_MAX 4096
#endif

extern char **environ;

/* -------------------------------------------------------------------------- *
 * Small utilities
 * -------------------------------------------------------------------------- */

static bool ends_with_ci(const char *s, const char *suffix) {
    size_t ls = strlen(s), lsuf = strlen(suffix);
    if (ls < lsuf) return false;
    const char *t = s + ls - lsuf;
    for (size_t i = 0; i < lsuf; i++)
        if (tolower((unsigned char)t[i]) != tolower((unsigned char)suffix[i])) return false;
    return true;
}

static bool starts_with(const char *s, const char *prefix) {
    return strncmp(s, prefix, strlen(prefix)) == 0;
}

static bool eq_ci(const char *a, const char *b) {
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return false;
        a++; b++;
    }
    return *a == *b;
}

static bool file_exists(const char *path) {
    struct stat st;
    if (stat(path, &st) != 0) return false;
    return S_ISREG(st.st_mode) || S_ISLNK(st.st_mode);
}

static void perr(const char *msg) {
    fputs("bsharp-c: ", stderr);
    fputs(msg, stderr);
    fputc('\n', stderr);
}

/* -------------------------------------------------------------------------- *
 * Property list (parsed -p:KEY=VALUE pairs)
 * -------------------------------------------------------------------------- */

typedef struct {
    char *key;
    char *value;
} kv_t;

typedef struct {
    kv_t *items;
    size_t n;
    size_t cap;
} kv_list_t;

static void kv_init(kv_list_t *l) { l->items = NULL; l->n = 0; l->cap = 0; }
static void kv_free(kv_list_t *l) {
    for (size_t i = 0; i < l->n; i++) { free(l->items[i].key); free(l->items[i].value); }
    free(l->items);
}

static bool kv_add(kv_list_t *l, const char *kv) {
    const char *eq = strchr(kv, '=');
    if (!eq || eq == kv) return false;
    if (l->n == l->cap) {
        l->cap = l->cap ? l->cap * 2 : 4;
        l->items = realloc(l->items, l->cap * sizeof(kv_t));
        if (!l->items) { perr("oom"); exit(127); }
    }
    size_t klen = (size_t)(eq - kv);
    char *k = malloc(klen + 1);
    char *v = strdup(eq + 1);
    if (!k || !v) { perr("oom"); exit(127); }
    memcpy(k, kv, klen); k[klen] = '\0';
    l->items[l->n].key = k;
    l->items[l->n].value = v;
    l->n++;
    return true;
}

static const char *kv_get(const kv_list_t *l, const char *key) {
    for (size_t i = 0; i < l->n; i++)
        if (eq_ci(l->items[i].key, key)) return l->items[i].value;
    return NULL;
}

static int kv_cmp(const void *a, const void *b) {
    const kv_t *ka = a, *kb = b;
    const char *p = ka->key, *q = kb->key;
    while (*p && *q) {
        int dp = tolower((unsigned char)*p), dq = tolower((unsigned char)*q);
        if (dp != dq) return dp - dq;
        p++; q++;
    }
    return (int)tolower((unsigned char)*p) - (int)tolower((unsigned char)*q);
}

/* -------------------------------------------------------------------------- *
 * Cache root resolution (must match C# HashGlobalPropertySet exactly).
 *
 * - Drop keys: TargetFramework, SuppressNETCoreSdkPreviewMessage,
 *   EnableSourceControlManagerQueries, EnableSourceLink
 * - If list empty: cache_root = .bsharp/
 * - Else: cache_root = .bsharp/variants/<sha256(sorted "KEY=VALUE\n").hex[:16]>
 * -------------------------------------------------------------------------- */

static bool is_cache_ignored_key(const char *k) {
    static const char *ignored[] = {
        "TargetFramework",
        "SuppressNETCoreSdkPreviewMessage",
        "EnableSourceControlManagerQueries",
        "EnableSourceLink",
        NULL,
    };
    for (int i = 0; ignored[i]; i++)
        if (eq_ci(k, ignored[i])) return true;
    return false;
}

static void resolve_cache_root(const char *bsharp_root, const kv_list_t *props, char *out, size_t outsz) {
    kv_list_t filtered;
    kv_init(&filtered);
    for (size_t i = 0; i < props->n; i++) {
        if (is_cache_ignored_key(props->items[i].key)) continue;
        kv_add(&filtered, "x=x"); /* placeholder; replaced just below */
        free(filtered.items[filtered.n - 1].key);
        free(filtered.items[filtered.n - 1].value);
        filtered.items[filtered.n - 1].key = strdup(props->items[i].key);
        filtered.items[filtered.n - 1].value = strdup(props->items[i].value);
    }
    if (filtered.n == 0) {
        strncpy(out, bsharp_root, outsz - 1);
        out[outsz - 1] = '\0';
        kv_free(&filtered);
        return;
    }

    qsort(filtered.items, filtered.n, sizeof(kv_t), kv_cmp);

    CC_SHA256_CTX ctx;
    CC_SHA256_Init(&ctx);
    for (size_t i = 0; i < filtered.n; i++) {
        CC_SHA256_Update(&ctx, filtered.items[i].key, (CC_LONG)strlen(filtered.items[i].key));
        CC_SHA256_Update(&ctx, "=", 1);
        CC_SHA256_Update(&ctx, filtered.items[i].value, (CC_LONG)strlen(filtered.items[i].value));
        CC_SHA256_Update(&ctx, "\n", 1);
    }
    unsigned char digest[CC_SHA256_DIGEST_LENGTH];
    CC_SHA256_Final(digest, &ctx);

    char hex[17];
    static const char H[] = "0123456789abcdef";
    for (int i = 0; i < 8; i++) {
        hex[i * 2]     = H[digest[i] >> 4];
        hex[i * 2 + 1] = H[digest[i] & 0xF];
    }
    hex[16] = '\0';

    snprintf(out, outsz, "%s/variants/%s", bsharp_root, hex);
    kv_free(&filtered);
}

/* -------------------------------------------------------------------------- *
 * Path helpers
 * -------------------------------------------------------------------------- */

static void path_join(char *out, size_t outsz, const char *a, const char *b) {
    snprintf(out, outsz, "%s/%s", a, b);
}

static void path_dirname(const char *path, char *out, size_t outsz) {
    const char *slash = strrchr(path, '/');
    if (!slash) { strncpy(out, ".", outsz - 1); out[outsz - 1] = '\0'; return; }
    size_t n = (size_t)(slash - path);
    if (n >= outsz) n = outsz - 1;
    memcpy(out, path, n);
    out[n] = '\0';
    if (n == 0) { strcpy(out, "/"); }
}

static void normalize_abs(const char *base, const char *rel, char *out, size_t outsz) {
    /* If rel is absolute, use as-is; else join with base. Result is collapsed
       lexically (handles "." and "..", no symlink resolution). */
    char tmp[PATH_MAX];
    if (rel[0] == '/') {
        strncpy(tmp, rel, sizeof(tmp) - 1);
    } else {
        snprintf(tmp, sizeof(tmp), "%s/%s", base, rel);
    }
    tmp[sizeof(tmp) - 1] = '\0';

    /* Replace backslashes with forward slashes (mirrors C# launcher). */
    for (char *p = tmp; *p; p++) if (*p == '\\') *p = '/';

    /* Lexical collapse: split on '/', stack of segments, handle "." and "..". */
    char *segs[128];
    int nsegs = 0;
    bool absolute = (tmp[0] == '/');
    char *p = tmp;
    while (*p) {
        if (*p == '/') { *p++ = '\0'; continue; }
        char *start = p;
        while (*p && *p != '/') p++;
        char saved = *p;
        if (saved) *p++ = '\0';
        if (strcmp(start, "") == 0 || strcmp(start, ".") == 0) {
            /* skip */
        } else if (strcmp(start, "..") == 0) {
            if (nsegs > 0) nsegs--;
        } else {
            if (nsegs >= (int)(sizeof(segs)/sizeof(segs[0]))) break;
            segs[nsegs++] = start;
        }
    }

    size_t pos = 0;
    if (absolute) out[pos++] = '/';
    for (int i = 0; i < nsegs; i++) {
        if (i > 0 || absolute) {
            if (i > 0) {
                if (pos + 1 < outsz) out[pos++] = '/';
            }
        }
        size_t l = strlen(segs[i]);
        if (pos + l < outsz) { memcpy(out + pos, segs[i], l); pos += l; }
    }
    if (pos == 0) { out[pos++] = '.'; }
    out[pos] = '\0';
}

/* -------------------------------------------------------------------------- *
 * Project discovery
 * -------------------------------------------------------------------------- */

static int find_csproj_in_cwd(char *out, size_t outsz) {
    char cwd[PATH_MAX];
    if (!getcwd(cwd, sizeof(cwd))) return -1;
    DIR *d = opendir(cwd);
    if (!d) return -1;
    int found = 0;
    char name[PATH_MAX] = {0};
    struct dirent *e;
    while ((e = readdir(d)) != NULL) {
        if (ends_with_ci(e->d_name, ".csproj")) {
            if (found++) { closedir(d); return -2; /* multiple */ }
            strncpy(name, e->d_name, sizeof(name) - 1);
        }
    }
    closedir(d);
    if (found != 1) return -1;
    snprintf(out, outsz, "%s/%s", cwd, name);
    return 0;
}

/* -------------------------------------------------------------------------- *
 * Static MSBuild path enumeration: find <Import Project="..."> and
 * <ProjectReference Include="..."> in csproj/props/targets files.
 *
 * Performance: ASCII-substring pre-check skips XML parsing entirely for
 * SDK-style csprojs that have neither. When parsing is needed, we do a
 * minimal hand-rolled scan (find element start, find attribute, extract
 * quoted value) — no XML library, no memory allocation per attribute.
 * -------------------------------------------------------------------------- */

static bool contains_ascii_fold(const char *hay, size_t hlen, const char *needle) {
    size_t nlen = strlen(needle);
    if (nlen == 0) return true;
    if (hlen < nlen) return false;
    char first = (char)tolower((unsigned char)needle[0]);
    for (size_t i = 0; i + nlen <= hlen; i++) {
        if ((char)tolower((unsigned char)hay[i]) != first) continue;
        bool ok = true;
        for (size_t j = 1; j < nlen; j++) {
            if ((char)tolower((unsigned char)hay[i + j]) != (char)tolower((unsigned char)needle[j])) {
                ok = false; break;
            }
        }
        if (ok) return true;
    }
    return false;
}

/* Read entire file into a malloc'd buffer; caller frees. Returns size or -1. */
static long read_all(const char *path, char **buf_out) {
    *buf_out = NULL;
    int fd = open(path, O_RDONLY);
    if (fd < 0) return -1;
    struct stat st;
    if (fstat(fd, &st) < 0 || !S_ISREG(st.st_mode)) { close(fd); return -1; }
    long sz = (long)st.st_size;
    char *buf = malloc((size_t)sz + 1);
    if (!buf) { close(fd); return -1; }
    long total = 0;
    while (total < sz) {
        ssize_t r = read(fd, buf + total, (size_t)(sz - total));
        if (r < 0) { if (errno == EINTR) continue; free(buf); close(fd); return -1; }
        if (r == 0) break;
        total += r;
    }
    close(fd);
    buf[total] = '\0';
    *buf_out = buf;
    return total;
}

/* Find a `<TAG ` element start, returning pointer to position right after
   the tag name (i.e., the first character of attribute area), or NULL. */
static const char *find_element_start(const char *hay, const char *end, const char *tag) {
    size_t tlen = strlen(tag);
    while (hay + 1 + tlen + 1 <= end) {
        const char *lt = memchr(hay, '<', (size_t)(end - hay));
        if (!lt || lt + 1 + tlen + 1 > end) return NULL;
        const char *p = lt + 1;
        bool match = true;
        for (size_t i = 0; i < tlen; i++) {
            if (tolower((unsigned char)p[i]) != tolower((unsigned char)tag[i])) { match = false; break; }
        }
        if (match) {
            char nxt = p[tlen];
            if (nxt == ' ' || nxt == '\t' || nxt == '\n' || nxt == '\r' || nxt == '/' || nxt == '>') {
                return p + tlen;
            }
        }
        hay = lt + 1;
    }
    return NULL;
}

/* From `attrs_start` scan to '>', extracting attribute `name`'s quoted value.
   Returns malloc'd value (caller frees) or NULL. */
static char *extract_attribute_value(const char *attrs_start, const char *end, const char *name) {
    size_t nlen = strlen(name);
    const char *p = attrs_start;
    while (p < end && *p != '>') {
        while (p < end && (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r')) p++;
        if (p >= end || *p == '>' || *p == '/') return NULL;
        const char *name_start = p;
        while (p < end && *p != '=' && *p != ' ' && *p != '\t' && *p != '\n' && *p != '\r' && *p != '>') p++;
        size_t this_nlen = (size_t)(p - name_start);
        bool match = (this_nlen == nlen);
        if (match) {
            for (size_t i = 0; i < nlen; i++) {
                if (tolower((unsigned char)name_start[i]) != tolower((unsigned char)name[i])) { match = false; break; }
            }
        }
        while (p < end && (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r')) p++;
        if (p >= end || *p != '=') {
            if (p < end && *p == '>') return NULL;
            continue;
        }
        p++; /* skip '=' */
        while (p < end && (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r')) p++;
        if (p >= end) return NULL;
        char quote = *p;
        if (quote != '"' && quote != '\'') return NULL;
        p++;
        const char *val_start = p;
        while (p < end && *p != quote) p++;
        if (p >= end) return NULL;
        if (match) {
            size_t vl = (size_t)(p - val_start);
            char *v = malloc(vl + 1);
            if (!v) return NULL;
            memcpy(v, val_start, vl);
            v[vl] = '\0';
            return v;
        }
        p++;
    }
    return NULL;
}

/* Resolve a static MSBuild path: skip if it contains $(, %( or wildcards;
   join to baseDir if relative; lexically canonicalize; require file exists.
   Returns malloc'd absolute path or NULL. */
static char *resolve_static_path(const char *base_dir, const char *value) {
    if (!value || !*value) return NULL;
    if (strstr(value, "$(") || strstr(value, "%(") || strchr(value, '*') || strchr(value, '?'))
        return NULL;
    char abs[PATH_MAX];
    normalize_abs(base_dir, value, abs, sizeof(abs));
    if (!file_exists(abs)) return NULL;
    return strdup(abs);
}

typedef struct {
    char **paths;
    size_t n;
    size_t cap;
} pathlist_t;

static void pl_init(pathlist_t *p) { p->paths = NULL; p->n = 0; p->cap = 0; }
static void pl_free(pathlist_t *p) {
    for (size_t i = 0; i < p->n; i++) free(p->paths[i]);
    free(p->paths);
}
static void pl_push(pathlist_t *p, char *s) {
    if (!s) return;
    if (p->n == p->cap) {
        p->cap = p->cap ? p->cap * 2 : 4;
        p->paths = realloc(p->paths, p->cap * sizeof(char *));
        if (!p->paths) { perr("oom"); exit(127); }
    }
    p->paths[p->n++] = s;
}

/* Scan an MSBuild file for static <Import Project="..."> and
   <ProjectReference Include="..."> children, resolving each to an absolute
   existing path. */
static int enumerate_static_paths(const char *file, pathlist_t *imports, pathlist_t *refs) {
    char *buf = NULL;
    long sz = read_all(file, &buf);
    if (sz < 0) return 0;
    bool has_import = contains_ascii_fold(buf, (size_t)sz, "<Import");
    bool has_ref    = contains_ascii_fold(buf, (size_t)sz, "<ProjectReference");
    if (!has_import && !has_ref) { free(buf); return 0; }

    char base_dir[PATH_MAX];
    path_dirname(file, base_dir, sizeof(base_dir));

    const char *cur = buf;
    const char *end = buf + sz;
    if (has_import) {
        const char *p = cur;
        while (p < end) {
            const char *attrs = find_element_start(p, end, "Import");
            if (!attrs) break;
            char *v = extract_attribute_value(attrs, end, "Project");
            if (v) {
                char *r = resolve_static_path(base_dir, v);
                pl_push(imports, r);
                free(v);
            }
            p = attrs;
        }
    }
    if (has_ref) {
        const char *p = cur;
        while (p < end) {
            const char *attrs = find_element_start(p, end, "ProjectReference");
            if (!attrs) break;
            char *v = extract_attribute_value(attrs, end, "Include");
            if (v) {
                char *r = resolve_static_path(base_dir, v);
                pl_push(refs, r);
                free(v);
            }
            p = attrs;
        }
    }
    free(buf);
    return 0;
}

/* -------------------------------------------------------------------------- *
 * Visited set (small open-address hash on lower-cased absolute paths).
 * -------------------------------------------------------------------------- */

typedef struct {
    char **paths;
    size_t cap;
    size_t n;
} visited_t;

static void vs_init(visited_t *v) { v->cap = 64; v->n = 0; v->paths = calloc(v->cap, sizeof(char *)); }
static void vs_free(visited_t *v) {
    for (size_t i = 0; i < v->cap; i++) free(v->paths[i]);
    free(v->paths);
}

static uint64_t fnv1a_ci(const char *s) {
    uint64_t h = 1469598103934665603ULL;
    for (; *s; s++) {
        h ^= (uint64_t)(unsigned char)tolower((unsigned char)*s);
        h *= 1099511628211ULL;
    }
    return h;
}

static bool vs_add(visited_t *v, const char *path) {
    if ((v->n + 1) * 2 > v->cap) {
        size_t newcap = v->cap * 2;
        char **np = calloc(newcap, sizeof(char *));
        for (size_t i = 0; i < v->cap; i++) {
            if (!v->paths[i]) continue;
            size_t idx = (size_t)(fnv1a_ci(v->paths[i]) & (newcap - 1));
            while (np[idx]) idx = (idx + 1) & (newcap - 1);
            np[idx] = v->paths[i];
        }
        free(v->paths);
        v->paths = np;
        v->cap = newcap;
    }
    size_t idx = (size_t)(fnv1a_ci(path) & (v->cap - 1));
    while (v->paths[idx]) {
        if (eq_ci(v->paths[idx], path)) return false;
        idx = (idx + 1) & (v->cap - 1);
    }
    v->paths[idx] = strdup(path);
    v->n++;
    return true;
}

/* -------------------------------------------------------------------------- *
 * Freshness check (mirrors C# IsHashFileStillFresh + Go importGraph/projectGraph).
 * -------------------------------------------------------------------------- */

static int newer_than(const char *path, time_t threshold_sec, long threshold_nsec) {
    struct stat st;
    if (stat(path, &st) != 0) {
        if (errno == ENOENT) return 0;
        return -1;
    }
#if defined(__APPLE__)
    time_t ms = st.st_mtimespec.tv_sec;
    long mns = st.st_mtimespec.tv_nsec;
#else
    time_t ms = st.st_mtim.tv_sec;
    long mns = st.st_mtim.tv_nsec;
#endif
    if (ms > threshold_sec) return 1;
    if (ms < threshold_sec) return 0;
    return (mns > threshold_nsec) ? 1 : 0;
}

static bool any_ancestor_file_is_newer(const char *proj_dir, time_t ts, long tns) {
    static const char *ancestor_files[] = {
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "NuGet.config",
        "global.json",
        NULL,
    };
    char dir[PATH_MAX];
    strncpy(dir, proj_dir, sizeof(dir) - 1);
    dir[sizeof(dir) - 1] = '\0';
    while (dir[0] && strcmp(dir, "/") != 0) {
        for (int i = 0; ancestor_files[i]; i++) {
            char p[PATH_MAX];
            path_join(p, sizeof(p), dir, ancestor_files[i]);
            int n = newer_than(p, ts, tns);
            if (n < 0) return true;
            if (n > 0) return true;
        }
        char parent[PATH_MAX];
        path_dirname(dir, parent, sizeof(parent));
        if (strcmp(parent, dir) == 0) break;
        strncpy(dir, parent, sizeof(dir) - 1);
        dir[sizeof(dir) - 1] = '\0';
    }
    return false;
}

static bool import_graph_fresh(const char *path, time_t ts, long tns, visited_t *proj_visited, visited_t *imp_visited);

static bool project_graph_fresh(const char *path, time_t ts, long tns, visited_t *proj_visited, visited_t *imp_visited) {
    char abs[PATH_MAX];
    normalize_abs("", path, abs, sizeof(abs));
    if (!vs_add(proj_visited, abs)) return true;

    if (newer_than(abs, ts, tns) > 0) return false;

    char proj_dir[PATH_MAX];
    path_dirname(abs, proj_dir, sizeof(proj_dir));

    char p[PATH_MAX];
    path_join(p, sizeof(p), proj_dir, "packages.lock.json");
    if (newer_than(p, ts, tns) > 0) return false;

    char obj_assets[PATH_MAX];
    snprintf(obj_assets, sizeof(obj_assets), "%s/obj/project.assets.json", proj_dir);
    if (newer_than(obj_assets, ts, tns) > 0) return false;

    if (any_ancestor_file_is_newer(proj_dir, ts, tns)) return false;

    pathlist_t imports, refs;
    pl_init(&imports);
    pl_init(&refs);
    enumerate_static_paths(abs, &imports, &refs);

    bool ok = true;
    for (size_t i = 0; ok && i < imports.n; i++)
        ok = import_graph_fresh(imports.paths[i], ts, tns, proj_visited, imp_visited);
    for (size_t i = 0; ok && i < refs.n; i++)
        ok = project_graph_fresh(refs.paths[i], ts, tns, proj_visited, imp_visited);

    pl_free(&imports);
    pl_free(&refs);
    return ok;
}

static bool import_graph_fresh(const char *path, time_t ts, long tns, visited_t *proj_visited, visited_t *imp_visited) {
    char abs[PATH_MAX];
    normalize_abs("", path, abs, sizeof(abs));
    if (!vs_add(imp_visited, abs)) return true;
    if (newer_than(abs, ts, tns) > 0) return false;

    pathlist_t imports, refs;
    pl_init(&imports);
    pl_init(&refs);
    enumerate_static_paths(abs, &imports, &refs);

    bool ok = true;
    for (size_t i = 0; ok && i < imports.n; i++)
        ok = import_graph_fresh(imports.paths[i], ts, tns, proj_visited, imp_visited);
    for (size_t i = 0; ok && i < refs.n; i++)
        ok = project_graph_fresh(refs.paths[i], ts, tns, proj_visited, imp_visited);

    pl_free(&imports);
    pl_free(&refs);
    return ok;
}

static bool is_hash_file_still_fresh(const char *project_path, const char *hash_file) {
    struct stat st;
    if (stat(hash_file, &st) != 0) return false;
#if defined(__APPLE__)
    time_t ts = st.st_mtimespec.tv_sec;
    long   tns = st.st_mtimespec.tv_nsec;
#else
    time_t ts = st.st_mtim.tv_sec;
    long   tns = st.st_mtim.tv_nsec;
#endif
    visited_t pv, iv;
    vs_init(&pv);
    vs_init(&iv);
    bool ok = project_graph_fresh(project_path, ts, tns, &pv, &iv);
    vs_free(&pv);
    vs_free(&iv);
    return ok;
}

/* -------------------------------------------------------------------------- *
 * Execve into host or fallback launcher.
 * -------------------------------------------------------------------------- */

static char self_exe[PATH_MAX];

static int resolve_self(void) {
#if defined(__APPLE__)
    uint32_t bufsz = sizeof(self_exe);
    extern int _NSGetExecutablePath(char *, uint32_t *);
    if (_NSGetExecutablePath(self_exe, &bufsz) != 0) return -1;
    char real[PATH_MAX];
    if (realpath(self_exe, real)) {
        strncpy(self_exe, real, sizeof(self_exe) - 1);
        self_exe[sizeof(self_exe) - 1] = '\0';
    }
    return 0;
#else
    ssize_t r = readlink("/proc/self/exe", self_exe, sizeof(self_exe) - 1);
    if (r <= 0) return -1;
    self_exe[r] = '\0';
    return 0;
#endif
}

static void set_taskd_env(void) {
    setenv("BSHARP_LAUNCHER_PATH", self_exe, 1);
    if (!getenv("BSHARP_TASKD_PATH")) {
        char dir[PATH_MAX];
        path_dirname(self_exe, dir, sizeof(dir));
        char p[PATH_MAX];
        path_join(p, sizeof(p), dir, "bsharp-taskd");
        if (file_exists(p)) setenv("BSHARP_TASKD_PATH", p, 1);
    }
}

static int exec_host(const char *bin, char *const original_argv[], int argc, int forward_start) {
    set_taskd_env();
    int newc = argc - forward_start + 1;
    char **argv = calloc((size_t)(newc + 1), sizeof(char *));
    argv[0] = (char *)bin;
    for (int i = 0; i < newc - 1; i++) argv[i + 1] = original_argv[forward_start + i];
    argv[newc] = NULL;
    execv(bin, argv);
    perror("bsharp-c: execv host");
    free(argv);
    return 127;
}

/* Fallback to C# launcher: forward the entire argv verbatim. */
static int fallback_to_csharp(char *const argv[], int argc) {
    const char *cs = getenv("BSHARP_CSHARP_LAUNCHER");
    char cs_buf[PATH_MAX];
    if (!cs || !*cs) {
        char dir[PATH_MAX];
        path_dirname(self_exe, dir, sizeof(dir));
        path_join(cs_buf, sizeof(cs_buf), dir, "bsharp");
        cs = cs_buf;
    }
    if (!file_exists(cs)) {
        perr("cannot find C# launcher fallback (set BSHARP_CSHARP_LAUNCHER)");
        return 127;
    }
    set_taskd_env();
    char **a = calloc((size_t)(argc + 2), sizeof(char *));
    a[0] = (char *)cs;
    for (int i = 1; i < argc; i++) a[i] = argv[i];
    a[argc] = NULL;
    execv(cs, a);
    perror("bsharp-c: execv csharp");
    free(a);
    return 127;
}

/* -------------------------------------------------------------------------- *
 * main: parse args, run fast-path or fall back.
 * -------------------------------------------------------------------------- */

int main(int argc, char *argv[]) {
    resolve_self();

    bool simple = true;
    const char *project_arg = NULL;
    kv_list_t props;
    kv_init(&props);

    /* fast-path argument scan: detect what we can/can't handle. forward_args
       are tracked implicitly — for the warm-path we forward args[1..] minus
       any --project parser shifts, but since the C# launcher accepts the
       same argv verbatim, we just hand original argv[1..] to the host. */
    for (int i = 1; i < argc; i++) {
        const char *a = argv[i];
        if (strcmp(a, "build") == 0 || strcmp(a, "run") == 0) {
            /* OK, fast-pathable command. */
        } else if (strcmp(a, "audit") == 0 || strcmp(a, "clean") == 0 || strcmp(a, "test") == 0) {
            simple = false;
        } else if (strcmp(a, "--no-cache") == 0) {
            simple = false;
        } else if (strcmp(a, "--no-restore") == 0 || strcmp(a, "--no-fast-noop") == 0) {
            /* pass-through */
        } else if (strcmp(a, "--background-codegen") == 0 ||
                   strcmp(a, "--bsharp-background-rebuild") == 0) {
            simple = false;
        } else if (strcmp(a, "--project") == 0 && i + 1 < argc) {
            project_arg = argv[++i];
        } else if ((strcmp(a, "-p") == 0 || strcmp(a, "--property") == 0) && i + 1 < argc) {
            if (!kv_add(&props, argv[++i])) simple = false;
        } else if (strcmp(a, "-t") == 0 || strcmp(a, "--target") == 0 || strcmp(a, "-target") == 0) {
            simple = false;
            i++;
        } else if (starts_with(a, "-p:") || starts_with(a, "/p:")) {
            if (!kv_add(&props, a + 3)) simple = false;
        } else if (starts_with(a, "--property:")) {
            if (!kv_add(&props, a + 11)) simple = false;
        } else if (starts_with(a, "-t:") || starts_with(a, "/t:") || starts_with(a, "--target:")) {
            simple = false;
        } else if (starts_with(a, "-v:") || starts_with(a, "/v:") || starts_with(a, "--verbosity:")) {
            /* pass-through */
        } else if (ends_with_ci(a, ".sln")) {
            simple = false;
        } else if (ends_with_ci(a, ".csproj")) {
            if (project_arg && strcmp(project_arg, a) != 0) simple = false;
            project_arg = a;
        }
        /* unknown tokens just pass through to the host. */
    }

    if (!simple) { kv_free(&props); return fallback_to_csharp(argv, argc); }

    char project_path[PATH_MAX];
    if (project_arg) {
        if (project_arg[0] == '/') {
            strncpy(project_path, project_arg, sizeof(project_path) - 1);
            project_path[sizeof(project_path) - 1] = '\0';
        } else {
            char cwd[PATH_MAX];
            if (!getcwd(cwd, sizeof(cwd))) { kv_free(&props); return fallback_to_csharp(argv, argc); }
            normalize_abs(cwd, project_arg, project_path, sizeof(project_path));
        }
        if (!file_exists(project_path)) { kv_free(&props); return fallback_to_csharp(argv, argc); }
    } else {
        if (find_csproj_in_cwd(project_path, sizeof(project_path)) != 0) {
            kv_free(&props);
            return fallback_to_csharp(argv, argc);
        }
    }

    char project_dir[PATH_MAX];
    path_dirname(project_path, project_dir, sizeof(project_dir));

    char bsharp_root[PATH_MAX];
    path_join(bsharp_root, sizeof(bsharp_root), project_dir, ".bsharp");

    char cache_root[PATH_MAX];
    resolve_cache_root(bsharp_root, &props, cache_root, sizeof(cache_root));

    char bsharp_dir[PATH_MAX];
    const char *tfm = kv_get(&props, "TargetFramework");
    if (tfm && *tfm) {
        char safe[PATH_MAX];
        size_t l = strlen(tfm);
        if (l >= sizeof(safe)) l = sizeof(safe) - 1;
        for (size_t i = 0; i < l; i++) safe[i] = (tfm[i] == '/' || tfm[i] == '\\') ? '_' : tfm[i];
        safe[l] = '\0';
        snprintf(bsharp_dir, sizeof(bsharp_dir), "%s/inner/%s", cache_root, safe);
    } else {
        strncpy(bsharp_dir, cache_root, sizeof(bsharp_dir) - 1);
        bsharp_dir[sizeof(bsharp_dir) - 1] = '\0';
    }

    char hash_file[PATH_MAX], bin_file[PATH_MAX];
    path_join(hash_file, sizeof(hash_file), bsharp_dir, "shape.hash");
    path_join(bin_file,  sizeof(bin_file),  bsharp_dir, "build");

    if (!file_exists(hash_file) || !file_exists(bin_file)) {
        kv_free(&props);
        return fallback_to_csharp(argv, argc);
    }
    if (!is_hash_file_still_fresh(project_path, hash_file)) {
        kv_free(&props);
        return fallback_to_csharp(argv, argc);
    }

    kv_free(&props);

    /* Warm fast-path hit: exec the host with the user's args (minus our binary
       name and the project arg the host will re-discover itself). The host
       accepts the same argv shape as the C# launcher's forward args. */
    return exec_host(bin_file, argv, argc, 1);
}
