// MBF/MBU assembly redirect resolver.
//
// We deliberately don't bundle Microsoft.Build.Framework / Microsoft.Build.Utilities.Core
// with the daemon. The SDK's task DLLs reference an older MBF (Version=15.1.0.0) but
// expect to bind against the SDK's bundled MBF, which is newer than any version we
// can stably bundle (it ships types like Microsoft.Build.Framework.IMultiThreadableTask
// that our older NuGet-packaged copy doesn't have).
//
// Approach: at runtime, redirect any request for MBF / MBU to the copy that lives
// next to the first task DLL we ever load. Both libraries always live in the same
// SDK directory as the SDK's task assemblies, so probing dirname(task DLL) is
// guaranteed to find a compatible version.
//
// This resolver is installed BEFORE any TaskExecutor type touches MBF symbols so
// the JIT-driven assembly load for the daemon's own typeof(ITask) etc. is satisfied
// by the SDK's MBF rather than failing.
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;

namespace Bsharp.Taskd;

internal static class AssemblyResolver {
    // Simple-name-only redirects, populated lazily once we know an SDK directory.
    static readonly ConcurrentDictionary<string, Assembly> Redirects = new(StringComparer.OrdinalIgnoreCase);

    // Probe directories registered via RegisterProbeDirectory. We resolve MBF/MBU
    // from the first dir that contains the matching DLL.
    static readonly List<string> ProbeDirs = new();
    static readonly object ProbeSync = new();

    static int _installed;

    public static void EnsureInstalled() {
        if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0) return;
        AssemblyLoadContext.Default.Resolving += OnResolving;
    }

    // The daemon registers the directory of every task DLL it sees. The first call
    // typically provides the SDK directory; subsequent calls are no-ops if the dir
    // is already known.
    public static void RegisterProbeDirectory(string dir) {
        if (string.IsNullOrEmpty(dir)) return;
        lock (ProbeSync) {
            foreach (var existing in ProbeDirs)
                if (string.Equals(existing, dir, StringComparison.OrdinalIgnoreCase))
                    return;
            ProbeDirs.Add(dir);
        }
    }

    static Assembly? OnResolving(AssemblyLoadContext ctx, AssemblyName name) {
        var simpleName = name.Name ?? "";
        if (simpleName.Length == 0) return null;

        if (Redirects.TryGetValue(simpleName, out var cached)) return cached;

        // Look up <simpleName>.dll in every registered probe dir. Snapshot under lock to
        // avoid mutation during enumeration.
        string[] dirs;
        lock (ProbeSync) dirs = ProbeDirs.ToArray();
        foreach (var dir in dirs) {
            var candidate = Path.Combine(dir, simpleName + ".dll");
            if (!File.Exists(candidate)) continue;
            try {
                var loaded = ctx.LoadFromAssemblyPath(candidate);
                Redirects[simpleName] = loaded;
                return loaded;
            } catch {
                // Try the next probe dir.
            }
        }

        // Fallback: scan the installed .NET SDKs (only for MSBuild assemblies — the
        // common case of the daemon's own MBF/MBU types being referenced before any
        // task DLL has registered a probe dir).
        if (IsMsbuildAssembly(simpleName)) {
            foreach (var sdkDir in EnumerateSdkDirs()) {
                var candidate = Path.Combine(sdkDir, simpleName + ".dll");
                if (!File.Exists(candidate)) continue;
                try {
                    var loaded = ctx.LoadFromAssemblyPath(candidate);
                    Redirects[simpleName] = loaded;
                    RegisterProbeDirectory(sdkDir);
                    return loaded;
                } catch {
                    // Try the next SDK dir.
                }
            }
        }

        return null;
    }

    static bool IsMsbuildAssembly(string simpleName) =>
        simpleName.StartsWith("Microsoft.Build", StringComparison.OrdinalIgnoreCase);

    // Enumerate <DOTNET_ROOT>/sdk/<version>/ directories sorted so that SDKs matching
    // the daemon's runtime major version come first (newest first within each band).
    // We use `typeof(object).Assembly.Location` to locate the runtime install, then
    // walk up to the dotnet root.
    static IEnumerable<string> EnumerateSdkDirs() {
        string? root = null;
        var env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "sdk"))) {
            root = env;
        } else {
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (!string.IsNullOrEmpty(runtimeDir)) {
                // .../shared/Microsoft.NETCore.App/<ver>/System.Private.CoreLib.dll → up 3 levels.
                var candidate = Path.GetFullPath(Path.Combine(runtimeDir!, "..", "..", ".."));
                if (Directory.Exists(Path.Combine(candidate, "sdk"))) root = candidate;
            }
        }
        if (root == null) yield break;
        var sdkRoot = Path.Combine(root, "sdk");
        string[] subs;
        try { subs = Directory.GetDirectories(sdkRoot); }
        catch { yield break; }

        var runtimeMajor = Environment.Version.Major;
        var matching = new List<(string dir, int[] parts)>();
        var others = new List<(string dir, int[] parts)>();
        foreach (var sub in subs) {
            var parts = ParseVersion(Path.GetFileName(sub));
            if (parts.Length > 0 && parts[0] == runtimeMajor) matching.Add((sub, parts));
            else others.Add((sub, parts));
        }
        matching.Sort((a, b) => CompareVersions(b.parts, a.parts));
        others.Sort((a, b) => CompareVersions(b.parts, a.parts));
        foreach (var (sub, _) in matching) yield return sub;
        foreach (var (sub, _) in others) yield return sub;
    }

    // Parse "11.0.100-preview.4.26230.115" → [11, 0, 100, 0, 4, 26230, 115] (preview suffix ignored
    // for ordering between same-prefix versions; numeric components after dashes are still parsed).
    static int[] ParseVersion(string s) {
        var pieces = s.Replace('-', '.').Split('.');
        var nums = new List<int>(pieces.Length);
        foreach (var p in pieces) {
            if (int.TryParse(p, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)) nums.Add(n);
            else nums.Add(0);
        }
        return nums.ToArray();
    }

    static int CompareVersions(int[] a, int[] b) {
        var len = Math.Max(a.Length, b.Length);
        for (var i = 0; i < len; i++) {
            var ai = i < a.Length ? a[i] : 0;
            var bi = i < b.Length ? b[i] : 0;
            if (ai != bi) return ai.CompareTo(bi);
        }
        return 0;
    }
}
