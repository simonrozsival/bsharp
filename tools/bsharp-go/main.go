// bsharp-go — Go-language launcher prototype for bsharp.
//
// Goal: validate how fast the launcher's warm fast-path can be when written
// in a language with near-zero process startup overhead and no managed runtime
// to initialize. The C# launcher (NativeAOT) gets to ~108ms warm-build wall
// time on console-net11. We want to see how much of that the launcher itself
// contributes vs. the host's wall time.
//
// Scope: feature parity with the C# launcher's *fast warm path* only:
//   - parse args (build/run; -p:, -t:, -v:, --no-restore, --no-cache, --no-fast-noop, project arg)
//   - find .csproj
//   - resolve `.bsharp/build` (handling -p: variants and -p:TargetFramework=)
//   - mtime-based freshness check (csproj + ancestor Directory.{Build.{props,targets},Packages.props}
//     + global.json + NuGet.config + packages.lock.json + obj/project.assets.json
//     + recursive Import/ProjectReference graph)
//   - on success: set env (BSHARP_LAUNCHER_PATH, BSHARP_TASKD_PATH) + execve into host binary
//   - on any cache miss / cold / unsupported: shell out to the C# launcher
//
// Everything else (cache rebuild, codegen, restore, audit, clean, test, multi-TFM
// outer build, background codegen, solution build) delegates to the C# launcher.
package main

import (
	"crypto/sha256"
	"encoding/xml"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"syscall"
	"time"
)

func main() {
	rc := run(os.Args[1:])
	os.Exit(rc)
}

func run(args []string) int {
	cmd, projectArg, globalProps, forwardArgs, simple := parseArgs(args)
	if !simple {
		return fallbackToCSharpLauncher(args)
	}

	projectPath, err := resolveProject(projectArg)
	if err != nil {
		// Let the C# launcher emit the standard error message and exit code.
		return fallbackToCSharpLauncher(args)
	}

	// Cache root resolution: .bsharp/ if no shaping globalProps, else .bsharp/variants/<hash>/
	// Then if -p:TargetFramework=X is set, append inner/<X>/
	projectDir := filepath.Dir(projectPath)
	bsharpRoot := filepath.Join(projectDir, ".bsharp")
	cacheRoot := resolveProjectCacheRoot(bsharpRoot, globalProps)
	bsharpDir := cacheRoot
	if tfm := lookupProp(globalProps, "TargetFramework"); tfm != "" {
		bsharpDir = filepath.Join(cacheRoot, "inner", sanitizePathSegment(tfm))
	}
	hashFile := filepath.Join(bsharpDir, "shape.hash")
	binFile := filepath.Join(bsharpDir, "build")

	if cmd == "audit" || cmd == "clean" || cmd == "test" {
		return fallbackToCSharpLauncher(args)
	}

	// Fast warm-cache path: hash file exists, bin file exists, no shape input is newer than hash file.
	if fileExists(hashFile) && fileExists(binFile) {
		fresh, err := isHashFileStillFresh(projectPath, hashFile)
		if err == nil && fresh {
			return execBuildBinary(binFile, forwardArgs)
		}
		// Either error during check or stale: let C# handle the rebuild path.
	}

	return fallbackToCSharpLauncher(args)
}

// ============================================================================
// Argument parsing
//
// Returns simple=true ONLY when every argument is one the Go launcher can
// confidently handle. Anything unexpected (a flag we don't recognize, a target
// list, a property syntax variant, --background-codegen, etc.) returns
// simple=false so we delegate to the C# launcher and stay safe.
// ============================================================================

func parseArgs(args []string) (cmd string, projectArg string, globalProps []propKV, forwardArgs []string, simple bool) {
	cmd = "build"
	simple = true
	for i := 0; i < len(args); i++ {
		a := args[i]
		switch a {
		case "build", "run":
			cmd = a
			forwardArgs = append(forwardArgs, a)
		case "audit", "clean", "test":
			cmd = a
			simple = false
		case "--no-cache":
			// Cache-disabled = always rebuild via C# launcher.
			simple = false
		case "--no-restore", "--no-fast-noop":
			forwardArgs = append(forwardArgs, a)
		case "--background-codegen", "--bsharp-background-rebuild":
			simple = false
		case "--project":
			if i+1 < len(args) {
				projectArg = args[i+1]
				i++
			}
		case "-p", "--property":
			if i+1 < len(args) {
				if !tryAddProp(&globalProps, args[i+1]) {
					simple = false
				}
				i++
			}
		case "-t", "--target", "-target":
			// Target lists feed into the shape hash; we don't recompute it on the
			// fast path. Defer to C# launcher.
			simple = false
		default:
			switch {
			case strings.HasPrefix(a, "-p:") || strings.HasPrefix(a, "/p:"):
				if !tryAddProp(&globalProps, a[3:]) {
					simple = false
				}
			case strings.HasPrefix(a, "--property:"):
				if !tryAddProp(&globalProps, a[len("--property:"):]) {
					simple = false
				}
			case strings.HasPrefix(a, "-t:") || strings.HasPrefix(a, "/t:") || strings.HasPrefix(a, "--target:"):
				simple = false
			case strings.HasPrefix(a, "-v:") || strings.HasPrefix(a, "/v:") || strings.HasPrefix(a, "--verbosity:"):
				forwardArgs = append(forwardArgs, a)
			case strings.HasSuffix(strings.ToLower(a), ".sln"):
				simple = false
			case strings.HasSuffix(a, ".csproj"):
				if projectArg != "" {
					simple = false
				}
				projectArg = a
				forwardArgs = append(forwardArgs, a)
			default:
				// Pass through arbitrary tokens like "Hello"-style forwarded args
				// to the host (they're harmless on the fast warm path; the host
				// rejects unknown options itself).
				forwardArgs = append(forwardArgs, a)
			}
		}
	}
	return
}

type propKV struct {
	Key   string
	Value string
}

func tryAddProp(props *[]propKV, kv string) bool {
	idx := strings.Index(kv, "=")
	if idx <= 0 {
		return false
	}
	*props = append(*props, propKV{Key: kv[:idx], Value: kv[idx+1:]})
	return true
}

func lookupProp(props []propKV, key string) string {
	for _, p := range props {
		if strings.EqualFold(p.Key, key) {
			return p.Value
		}
	}
	return ""
}

// ============================================================================
// Cache directory resolution (must match C# launcher's hashing exactly).
//
// C# does:
//   ignoreKeys = {TargetFramework, SuppressNETCoreSdkPreviewMessage,
//                 EnableSourceControlManagerQueries, EnableSourceLink}
//   filteredProps = props except ignoreKeys
//   if empty -> .bsharp/
//   else     -> .bsharp/variants/<sha256(sorted "KEY=VALUE\n" lines).hex[:16]>
// ============================================================================

var cacheIgnoreKeys = map[string]struct{}{
	"targetframework":                       {},
	"suppressnetcoresdkpreviewmessage":      {},
	"enablesourcecontrolmanagerqueries":     {},
	"enablesourcelink":                      {},
}

func resolveProjectCacheRoot(bsharpRoot string, props []propKV) string {
	var cacheProps []propKV
	for _, p := range props {
		if _, skip := cacheIgnoreKeys[strings.ToLower(p.Key)]; skip {
			continue
		}
		cacheProps = append(cacheProps, p)
	}
	if len(cacheProps) == 0 {
		return bsharpRoot
	}
	sort.SliceStable(cacheProps, func(i, j int) bool {
		return strings.ToLower(cacheProps[i].Key) < strings.ToLower(cacheProps[j].Key)
	})
	h := sha256.New()
	for _, p := range cacheProps {
		fmt.Fprintf(h, "%s=%s\n", p.Key, p.Value)
	}
	hex := fmt.Sprintf("%x", h.Sum(nil))[:16]
	return filepath.Join(bsharpRoot, "variants", hex)
}

func sanitizePathSegment(s string) string {
	// C# uses Path.GetInvalidFileNameChars; on macOS that's essentially '/' and '\0'.
	r := strings.NewReplacer("/", "_", "\\", "_", string(byte(0)), "_")
	return r.Replace(s)
}

// ============================================================================
// Project resolution: find a .csproj in the working dir if not specified.
// ============================================================================

func resolveProject(projectArg string) (string, error) {
	if projectArg != "" {
		abs, err := filepath.Abs(projectArg)
		if err != nil {
			return "", err
		}
		if !fileExists(abs) {
			return "", fmt.Errorf("project not found: %s", projectArg)
		}
		return abs, nil
	}
	wd, err := os.Getwd()
	if err != nil {
		return "", err
	}
	entries, err := os.ReadDir(wd)
	if err != nil {
		return "", err
	}
	var found string
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		if strings.HasSuffix(name, ".csproj") {
			if found != "" {
				return "", errors.New("multiple .csproj files in current directory")
			}
			found = filepath.Join(wd, name)
		}
	}
	if found == "" {
		return "", errors.New("no .csproj found")
	}
	return found, nil
}

// ============================================================================
// Freshness check (mirrors C# IsHashFileStillFresh).
//
// For each project in the static graph rooted at projectPath:
//   - csproj itself must not be newer than the hash file
//   - packages.lock.json, obj/project.assets.json (if exist)
//   - in EVERY ancestor dir up to filesystem root: Directory.Build.props,
//     Directory.Build.targets, Directory.Packages.props, NuGet.config, global.json
//   - recurse on every static <Import Project="...">
//   - recurse on every static <ProjectReference Include="...">
// ============================================================================

func isHashFileStillFresh(projectPath, hashFile string) (bool, error) {
	hashStat, err := os.Stat(hashFile)
	if err != nil {
		return false, err
	}
	threshold := hashStat.ModTime()
	visitedProjects := map[string]struct{}{}
	visitedImports := map[string]struct{}{}
	return projectGraphStillFresh(projectPath, threshold, visitedProjects, visitedImports)
}

func projectGraphStillFresh(path string, threshold time.Time, visitedProjects, visitedImports map[string]struct{}) (bool, error) {
	path, err := filepath.Abs(path)
	if err != nil {
		return false, err
	}
	if _, seen := visitedProjects[strings.ToLower(path)]; seen {
		return true, nil
	}
	visitedProjects[strings.ToLower(path)] = struct{}{}

	if newer, err := newerThan(path, threshold); err != nil {
		return false, err
	} else if newer {
		return false, nil
	}

	projDir := filepath.Dir(path)
	for _, sibling := range []string{
		filepath.Join(projDir, "packages.lock.json"),
		filepath.Join(projDir, "obj", "project.assets.json"),
	} {
		if newer, err := newerThan(sibling, threshold); err != nil {
			return false, err
		} else if newer {
			return false, nil
		}
	}

	for d := projDir; d != "" && d != "/"; d = filepath.Dir(d) {
		for _, f := range []string{
			"Directory.Build.props",
			"Directory.Build.targets",
			"Directory.Packages.props",
			"NuGet.config",
			"global.json",
		} {
			if newer, err := newerThan(filepath.Join(d, f), threshold); err != nil {
				return false, err
			} else if newer {
				return false, nil
			}
		}
		parent := filepath.Dir(d)
		if parent == d {
			break
		}
	}

	imports, references, err := enumerateStaticMsBuildPaths(path)
	if err != nil {
		return false, err
	}
	for _, imp := range imports {
		ok, err := importGraphStillFresh(imp, threshold, visitedProjects, visitedImports)
		if err != nil || !ok {
			return ok, err
		}
	}
	for _, ref := range references {
		ok, err := projectGraphStillFresh(ref, threshold, visitedProjects, visitedImports)
		if err != nil || !ok {
			return ok, err
		}
	}
	return true, nil
}

func importGraphStillFresh(path string, threshold time.Time, visitedProjects, visitedImports map[string]struct{}) (bool, error) {
	path, err := filepath.Abs(path)
	if err != nil {
		return false, err
	}
	if _, seen := visitedImports[strings.ToLower(path)]; seen {
		return true, nil
	}
	visitedImports[strings.ToLower(path)] = struct{}{}

	if newer, err := newerThan(path, threshold); err != nil {
		return false, err
	} else if newer {
		return false, nil
	}

	imports, references, err := enumerateStaticMsBuildPaths(path)
	if err != nil {
		return false, err
	}
	for _, imp := range imports {
		ok, err := importGraphStillFresh(imp, threshold, visitedProjects, visitedImports)
		if err != nil || !ok {
			return ok, err
		}
	}
	for _, ref := range references {
		ok, err := projectGraphStillFresh(ref, threshold, visitedProjects, visitedImports)
		if err != nil || !ok {
			return ok, err
		}
	}
	return true, nil
}

func newerThan(path string, threshold time.Time) (bool, error) {
	st, err := os.Stat(path)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return false, nil
		}
		return false, err
	}
	return st.ModTime().After(threshold), nil
}

// ============================================================================
// Static MSBuild path extraction (mirrors C# EnumerateStaticMsBuildPaths +
// the substring fast-path I just added in the C# launcher).
// ============================================================================

func enumerateStaticMsBuildPaths(filePath string) (imports, references []string, err error) {
	data, err := os.ReadFile(filePath)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return nil, nil, nil
		}
		return nil, nil, err
	}
	// Substring pre-check (same logic as the C# launcher): SDK-style csprojs have
	// no static <Import> / <ProjectReference>, so we can skip XML parsing.
	hasImport := containsAsciiFold(data, "<Import")
	hasRef := containsAsciiFold(data, "<ProjectReference")
	if !hasImport && !hasRef {
		return nil, nil, nil
	}

	baseDir := filepath.Dir(filePath)
	decoder := xml.NewDecoder(strings.NewReader(string(data)))
	for {
		tok, err := decoder.Token()
		if err != nil {
			break
		}
		se, ok := tok.(xml.StartElement)
		if !ok {
			continue
		}
		switch strings.ToLower(se.Name.Local) {
		case "import":
			if hasImport {
				if v := attrValueFold(se.Attr, "Project"); v != "" {
					if resolved := resolveStaticMsBuildPath(baseDir, v); resolved != "" {
						imports = append(imports, resolved)
					}
				}
			}
		case "projectreference":
			if hasRef {
				if v := attrValueFold(se.Attr, "Include"); v != "" {
					if resolved := resolveStaticMsBuildPath(baseDir, v); resolved != "" {
						references = append(references, resolved)
					}
				}
			}
		}
	}
	return imports, references, nil
}

func attrValueFold(attrs []xml.Attr, name string) string {
	for _, a := range attrs {
		if strings.EqualFold(a.Name.Local, name) {
			return a.Value
		}
	}
	return ""
}

func resolveStaticMsBuildPath(baseDir, value string) string {
	value = strings.TrimSpace(value)
	if value == "" {
		return ""
	}
	if strings.Contains(value, "$(") || strings.Contains(value, "%(") || strings.ContainsAny(value, "*?") {
		return ""
	}
	value = strings.ReplaceAll(value, `\`, string(filepath.Separator))
	var path string
	if filepath.IsAbs(value) {
		path = value
	} else {
		path = filepath.Join(baseDir, value)
	}
	abs, err := filepath.Abs(path)
	if err != nil {
		return ""
	}
	if !fileExists(abs) {
		return ""
	}
	return abs
}

func containsAsciiFold(haystack []byte, needle string) bool {
	if len(needle) == 0 {
		return true
	}
	if len(haystack) < len(needle) {
		return false
	}
	first := toLowerAscii(needle[0])
	end := len(haystack) - len(needle)
	for i := 0; i <= end; i++ {
		if toLowerAscii(haystack[i]) != first {
			continue
		}
		matched := true
		for j := 1; j < len(needle); j++ {
			if toLowerAscii(haystack[i+j]) != toLowerAscii(needle[j]) {
				matched = false
				break
			}
		}
		if matched {
			return true
		}
	}
	return false
}

func toLowerAscii(b byte) byte {
	if 'A' <= b && b <= 'Z' {
		return b + 32
	}
	return b
}

func fileExists(path string) bool {
	st, err := os.Stat(path)
	return err == nil && !st.IsDir()
}

// ============================================================================
// Exec into the host binary, replacing this process.
//
// Also propagate BSHARP_LAUNCHER_PATH and BSHARP_TASKD_PATH like the C#
// launcher does.
// ============================================================================

func execBuildBinary(binFile string, forwardArgs []string) int {
	setBuildBinaryEnvironment()
	if runtime.GOOS != "windows" {
		argv := append([]string{binFile}, forwardArgs...)
		envv := os.Environ()
		// syscall.Exec replaces this process; on success it does not return.
		if err := syscall.Exec(binFile, argv, envv); err != nil {
			fmt.Fprintf(os.Stderr, "bsharp-go: exec %s failed: %v\n", binFile, err)
		}
		// Fall through to Process.Start equivalent if execve failed.
	}
	// Fallback: spawn + wait.
	return spawnAndWait(binFile, forwardArgs)
}

func setBuildBinaryEnvironment() {
	if launcherPath, err := os.Executable(); err == nil {
		os.Setenv("BSHARP_LAUNCHER_PATH", launcherPath)
		if os.Getenv("BSHARP_TASKD_PATH") == "" {
			launcherDir := filepath.Dir(realPath(launcherPath))
			taskd := filepath.Join(launcherDir, "bsharp-taskd")
			if runtime.GOOS == "windows" {
				taskd += ".exe"
			}
			if fileExists(taskd) {
				os.Setenv("BSHARP_TASKD_PATH", taskd)
			}
		}
	}
}

func realPath(p string) string {
	if resolved, err := filepath.EvalSymlinks(p); err == nil {
		return resolved
	}
	return p
}

func spawnAndWait(binFile string, forwardArgs []string) int {
	// Minimal fork+wait: only used if execve fails (extremely unlikely on macOS/Linux).
	pid, err := syscall.ForkExec(binFile, append([]string{binFile}, forwardArgs...), &syscall.ProcAttr{
		Env:   os.Environ(),
		Files: []uintptr{os.Stdin.Fd(), os.Stdout.Fd(), os.Stderr.Fd()},
	})
	if err != nil {
		fmt.Fprintf(os.Stderr, "bsharp-go: spawn %s failed: %v\n", binFile, err)
		return 127
	}
	var ws syscall.WaitStatus
	if _, err := syscall.Wait4(pid, &ws, 0, nil); err != nil {
		return 127
	}
	return ws.ExitStatus()
}

// ============================================================================
// Fallback to the C# launcher for anything the Go launcher doesn't handle.
//
// The Go launcher is intentionally narrow: warm cache hits only. Anything else
// (cache miss, codegen, restore, audit, clean, test, multi-TFM outer dispatch,
// solution build, etc.) goes back to the C# launcher which has the full
// implementation.
// ============================================================================

func fallbackToCSharpLauncher(args []string) int {
	csharpPath := resolveCSharpLauncher()
	if csharpPath == "" {
		fmt.Fprintf(os.Stderr, "bsharp-go: cannot find C# launcher fallback (set BSHARP_CSHARP_LAUNCHER)\n")
		return 127
	}
	setBuildBinaryEnvironment()
	if runtime.GOOS != "windows" {
		argv := append([]string{csharpPath}, args...)
		envv := os.Environ()
		if err := syscall.Exec(csharpPath, argv, envv); err != nil {
			fmt.Fprintf(os.Stderr, "bsharp-go: exec C# launcher failed: %v\n", err)
		}
	}
	return spawnAndWait(csharpPath, args)
}

func resolveCSharpLauncher() string {
	if p := os.Getenv("BSHARP_CSHARP_LAUNCHER"); p != "" {
		return p
	}
	// Sibling layout: <dir>/bsharp-go and <dir>/bsharp side by side.
	if self, err := os.Executable(); err == nil {
		dir := filepath.Dir(realPath(self))
		c := filepath.Join(dir, "bsharp")
		if runtime.GOOS == "windows" {
			c += ".exe"
		}
		if fileExists(c) {
			return c
		}
	}
	return ""
}
