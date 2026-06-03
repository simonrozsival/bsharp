// bsharp — the launcher. Native AOT.
// Locates the project, checks the .bsharp/ cache, codegens + publishes if stale,
// then execs the per-project compiled binary.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

return Launcher.Run(args);

static class Launcher {
    const string BackgroundCodegenEnvironmentVariable = "BSHARP_BACKGROUND_CODEGEN";
    const string BackgroundRebuildCommand = "--bsharp-background-rebuild";
    const string ShapeHashVersion = "bsharp-shape-v12-ordered-deps-parallel-outer";

    public static int Run(string[] args) {
        if (args.Length > 0 && string.Equals(args[0], BackgroundRebuildCommand, StringComparison.Ordinal))
            return RunBackgroundRebuild(args.Skip(1).ToArray());

        string command = "build";
        bool noCache = false;
        bool noFastRestore = false;
        bool backgroundCodegen = IsBackgroundCodegenEnabledByEnvironment();
        string? projectArg = null;
        var forwardArgs = new List<string>();
        var globalProps = new List<KeyValuePair<string, string>>();
        var requestedTargets = new List<string>();
        for (int i = 0; i < args.Length; i++) {
            var a = args[i];
            switch (a) {
                case "build": case "run": case "audit": case "clean": case "test":
                    command = a;
                    if (a != "audit")
                        forwardArgs.Add(a);
                    break;
                case "--no-cache":
                    noCache = true;
                    break;
                case "--no-fast-restore":
                    noFastRestore = true;
                    break;
                case "--background-codegen":
                    backgroundCodegen = true;
                    break;
                case "--project":
                    if (i + 1 < args.Length) projectArg = args[++i];
                    break;
                case "--property" or "-p":
                    if (i + 1 < args.Length) TryAddProp(globalProps, args[++i]);
                    break;
                case "-t" or "--target" or "-target":
                    forwardArgs.Add(a);
                    if (i + 1 < args.Length) {
                        var targets = args[++i];
                        AddTargets(requestedTargets, targets);
                        forwardArgs.Add(targets);
                    }
                    break;
                default:
                    if (a.StartsWith("-p:", StringComparison.Ordinal) || a.StartsWith("/p:", StringComparison.Ordinal)) {
                        TryAddProp(globalProps, a.Substring(3));
                    } else if (a.StartsWith("--property:", StringComparison.Ordinal)) {
                        TryAddProp(globalProps, a.Substring("--property:".Length));
                    } else if (TryAddTargetsFromArg(requestedTargets, a)) {
                        forwardArgs.Add(a);
                    } else if (a.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && projectArg == null) {
                        // Solution file - build all projects
                        return BuildSolution(a, command, forwardArgs.ToArray(), globalProps, noCache, backgroundCodegen);
                    } else if (a.EndsWith(".csproj", StringComparison.Ordinal) && projectArg == null) {
                        projectArg = a;
                        forwardArgs.Add(a);
                    } else {
                        forwardArgs.Add(a);
                    }
                    break;
            }
        }
        ApplyBsharpDefaultGlobalProperties(globalProps);

        // Sort for hash stability.
        globalProps.Sort((x, y) => string.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase));

        // Check if current directory has a .sln file
        string? solutionPath = ResolveSolution(null);
        if (solutionPath != null && projectArg == null) {
            Console.WriteLine($"bsharp: building solution {Path.GetFileName(solutionPath)}");
            return BuildSolution(solutionPath, command, forwardArgs.ToArray(), globalProps, noCache, backgroundCodegen);
        }

        string? projectPath = ResolveProject(projectArg);
        if (projectPath == null) {
            Console.Error.WriteLine("bsharp: no .csproj or .sln found in current directory (and none specified)");
            return 2;
        }
        NormalizeForwardedProjectPath(forwardArgs, projectPath);

        if (command == "clean")
            return CleanProject(projectPath);

        if (command == "test")
            return TestProject(projectPath, forwardArgs.ToArray());

        string projectDir = Path.GetDirectoryName(projectPath)!;
        string bsharpRoot = Path.Combine(projectDir, ".bsharp");
        string projectCacheRoot = ResolveProjectCacheRoot(bsharpRoot, globalProps);
        string bsharpDir = projectCacheRoot;
        if (TryGetGlobalProp(globalProps, "TargetFramework", out var explicitTargetFramework) && !string.IsNullOrWhiteSpace(explicitTargetFramework))
            bsharpDir = Path.Combine(projectCacheRoot, "inner", SanitizePathSegment(explicitTargetFramework));
        string hashFile = Path.Combine(bsharpDir, "shape.hash");
        string binFile = Path.Combine(bsharpDir, "build");

        if (command == "audit")
            return RunAudit(projectPath, globalProps);

        var restoredWithCachedHost = false;
        if (noFastRestore && !HasNoRestore(forwardArgs)) {
            var restoreRc = RestoreProject(projectPath, globalProps, "restoring project (--no-fast-restore)");
            if (restoreRc != 0)
                return restoreRc;
            AddNoRestore(forwardArgs);
        } else if (ShouldPreRestoreProjectReferences(projectPath, forwardArgs)) {
            var restoreRc = RestoreProject(projectPath, globalProps, "restoring ProjectReference graph");
            if (restoreRc != 0)
                return restoreRc;
            AddNoRestore(forwardArgs);
        } else if (ShouldRestoreMissingAssetsBeforeHash(projectPath, forwardArgs)) {
            if (!noCache && File.Exists(hashFile) && File.Exists(binFile)) {
                var cachedRestoreRc = RestoreMissingAssetsWithCachedHost(binFile, projectPath);
                if (cachedRestoreRc == 0) {
                    restoredWithCachedHost = true;
                    AddNoRestore(forwardArgs);
                }
            }

            if (!restoredWithCachedHost) {
                var restoreRc = RestoreProject(projectPath, globalProps, "restoring missing project assets before cache check");
                if (restoreRc != 0)
                    return restoreRc;
                AddNoRestore(forwardArgs);
            }
        }

        // Fast path: if cached build exists and no shape input is newer than the cache, trust it.
        if (!noCache && File.Exists(hashFile) && File.Exists(binFile)) {
            if (IsHashFileStillFresh(projectPath, hashFile)) {
                return ExecBuildBinary(binFile, forwardArgs);
            }
        }

        // Slow path: compute full shape hash to validate cache
        string currentHash = ComputeShapeHash(projectPath, globalProps, requestedTargets);

        // Cache hit?
        if (!noCache && File.Exists(hashFile) && File.Exists(binFile)) {
            // shape.hash is "<hex>\n<mode>\n"; only the first line is the content hash.
            var cached = File.ReadAllText(hashFile).Split('\n', 2)[0].Trim();
            if (string.Equals(cached, currentHash, StringComparison.Ordinal)) {
                return ExecBuildBinaryAndRefreshShapeHash(binFile, forwardArgs, projectPath, globalProps, requestedTargets, hashFile);
            }
        }

        if (restoredWithCachedHost) {
            var restoreRc = RestoreProject(projectPath, globalProps, "restoring current project assets after cache miss");
            if (restoreRc != 0)
                return restoreRc;
            currentHash = ComputeShapeHash(projectPath, globalProps, requestedTargets);
        }

        // Cache miss — regenerate.
        if (backgroundCodegen && !noCache) {
            StartBackgroundRebuildIfNeeded(projectPath, projectCacheRoot, bsharpDir, currentHash, globalProps, requestedTargets);
            var fallbackRc = RunDotnetFallback(command, projectPath, forwardArgs, globalProps);
            if (fallbackRc == 0 && File.Exists(hashFile))
                RefreshShapeHash(projectPath, globalProps, requestedTargets, hashFile);
            return fallbackRc;
        }

        int rebuildRc;
        if (bsharpDir == projectCacheRoot
            && TryEvaluateTargetFrameworkInfo(projectPath, globalProps, out var frameworkInfo)
            && string.IsNullOrEmpty(frameworkInfo.TargetFramework)
            && frameworkInfo.TargetFrameworks.Length > 0)
        {
            rebuildRc = RebuildOuter(projectPath, projectCacheRoot, currentHash, globalProps, requestedTargets, frameworkInfo.TargetFrameworks);
        }
        else
        {
            rebuildRc = Rebuild(projectPath, bsharpDir, currentHash, globalProps, requestedTargets);
        }
        if (rebuildRc != 0) return rebuildRc;
        return ExecBuildBinaryAndRefreshShapeHash(binFile, forwardArgs, projectPath, globalProps, requestedTargets, hashFile);
    }

    static bool IsBackgroundCodegenEnabledByEnvironment() {
        var value = Environment.GetEnvironmentVariable(BackgroundCodegenEnvironmentVariable);
        return value is "1"
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    static void NormalizeForwardedProjectPath(List<string> forwardArgs, string projectPath) {
        for (var i = 0; i < forwardArgs.Count; i++) {
            var arg = forwardArgs[i];
            if (!arg.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;
            try {
                if (string.Equals(Path.GetFullPath(arg), projectPath, StringComparison.OrdinalIgnoreCase))
                    forwardArgs[i] = projectPath;
            } catch {
                // Keep unusual arguments unchanged.
            }
        }
    }

    static string ResolveProjectCacheRoot(string bsharpRoot, List<KeyValuePair<string, string>> globalProps) {
        // Ignore properties that don't affect build shape (just optimization/noise flags)
        var ignoreKeys = new[] { "TargetFramework", "SuppressNETCoreSdkPreviewMessage", "EnableSourceControlManagerQueries", "EnableSourceLink" };
        var cacheProps = globalProps
            .Where(p => !ignoreKeys.Any(k => string.Equals(p.Key, k, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (cacheProps.Length == 0)
            return bsharpRoot;
        return Path.Combine(bsharpRoot, "variants", HashGlobalPropertySet(cacheProps));
    }

    static string HashGlobalPropertySet(IReadOnlyList<KeyValuePair<string, string>> props) {
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        foreach (var kv in props.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) {
            var bytes = Encoding.UTF8.GetBytes($"{kv.Key}={kv.Value}\n");
            ms.Write(bytes);
        }
        return Convert.ToHexString(sha.ComputeHash(ms.ToArray())).ToLowerInvariant()[..16];
    }

    static bool ShouldPreRestoreProjectReferences(string projectPath, List<string> forwardArgs) =>
        !HasNoRestore(forwardArgs) &&
        HasStaticProjectReferences(projectPath) &&
        HasMissingOrStaleProjectAssetsInStaticGraph(projectPath);

    static bool ShouldRestoreMissingAssetsBeforeHash(string projectPath, List<string> forwardArgs) =>
        !HasNoRestore(forwardArgs) && HasMissingProjectAssetsInStaticGraph(projectPath);

    static bool HasNoRestore(List<string> args) =>
        args.Any(arg => string.Equals(arg, "--no-restore", StringComparison.OrdinalIgnoreCase));

    static void AddNoRestore(List<string> args) {
        if (!HasNoRestore(args))
            args.Add("--no-restore");
    }

    static bool HasMissingProjectAssetsInStaticGraph(string projectPath) {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string path) {
            path = Path.GetFullPath(path);
            if (!visited.Add(path))
                return false;

            var dir = Path.GetDirectoryName(path)!;
            if (!File.Exists(Path.Combine(dir, "obj", "project.assets.json")))
                return true;

            foreach (var importPath in EnumerateStaticMsBuildPaths(path, "Import", "Project")) {
                if (Visit(importPath))
                    return true;
            }
            foreach (var referencePath in EnumerateStaticMsBuildPaths(path, "ProjectReference", "Include")) {
                if (Visit(referencePath))
                    return true;
            }

            return false;
        }

        return Visit(projectPath);
    }

    static bool HasMissingOrStaleProjectAssetsInStaticGraph(string projectPath) {
        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool VisitProject(string path) {
            path = Path.GetFullPath(path);
            if (!visitedProjects.Add(path))
                return false;

            var dir = Path.GetDirectoryName(path)!;
            var assetsPath = Path.Combine(dir, "obj", "project.assets.json");
            if (!File.Exists(assetsPath))
                return true;

            var assetsTime = File.GetLastWriteTimeUtc(assetsPath);
            if (IsRestoreInputNewerThanAssets(path, assetsTime))
                return true;

            foreach (var importPath in EnumerateStaticMsBuildPaths(path, "Import", "Project")) {
                if (VisitImport(importPath, assetsTime))
                    return true;
            }
            foreach (var referencePath in EnumerateStaticMsBuildPaths(path, "ProjectReference", "Include")) {
                if (VisitProject(referencePath))
                    return true;
            }

            return false;
        }

        bool VisitImport(string path, DateTime ownerAssetsTime) {
            path = Path.GetFullPath(path);
            if (!visitedImports.Add(path))
                return false;

            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > ownerAssetsTime)
                return true;

            foreach (var importPath in EnumerateStaticMsBuildPaths(path, "Import", "Project")) {
                if (VisitImport(importPath, ownerAssetsTime))
                    return true;
            }
            foreach (var referencePath in EnumerateStaticMsBuildPaths(path, "ProjectReference", "Include")) {
                if (VisitProject(referencePath))
                    return true;
            }

            return false;
        }

        return VisitProject(projectPath);
    }

    static bool IsRestoreInputNewerThanAssets(string projectPath, DateTime assetsTime) {
        if (File.GetLastWriteTimeUtc(projectPath) > assetsTime)
            return true;

        var dir = Path.GetDirectoryName(projectPath);
        while (!string.IsNullOrEmpty(dir)) {
            if (IsNewerThan(Path.Combine(dir, "Directory.Build.props"), assetsTime)) return true;
            if (IsNewerThan(Path.Combine(dir, "Directory.Build.targets"), assetsTime)) return true;
            if (IsNewerThan(Path.Combine(dir, "Directory.Packages.props"), assetsTime)) return true;
            if (IsNewerThan(Path.Combine(dir, "NuGet.config"), assetsTime)) return true;
            if (IsNewerThan(Path.Combine(dir, "global.json"), assetsTime)) return true;
            dir = Path.GetDirectoryName(dir);
        }

        var projectDir = Path.GetDirectoryName(projectPath)!;
        if (IsNewerThan(Path.Combine(projectDir, "packages.lock.json"), assetsTime)) return true;
        return false;
    }

    static bool IsNewerThan(string path, DateTime threshold) =>
        File.Exists(path) && File.GetLastWriteTimeUtc(path) > threshold;

    static bool HasStaticProjectReferences(string projectPath) {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string path) {
            path = Path.GetFullPath(path);
            if (!visited.Add(path))
                return false;

            if (EnumerateStaticMsBuildPaths(path, "ProjectReference", "Include").Any())
                return true;

            foreach (var importPath in EnumerateStaticMsBuildPaths(path, "Import", "Project")) {
                if (Visit(importPath))
                    return true;
            }

            return false;
        }

        return Visit(projectPath);
    }

    static int RestoreMissingAssetsWithCachedHost(string binFile, string projectPath) {
        if (!HasDiscoverableTaskDaemon())
            return 1;

        Console.Error.WriteLine("bsharp: restoring missing project assets with cached bsharp host...");
        var sw = Stopwatch.StartNew();
        // Use the non-execv path here: we need to return to the launcher to
        // continue with the actual build/run after the restore completes.
        var rc = RunBuildBinaryAndWaitQuiet(binFile, new List<string> { "restore", projectPath, "-v:quiet" }, out var output);
        sw.Stop();
        if (rc != 0) {
            Console.Error.WriteLine($"bsharp: cached bsharp restore failed (exit {rc}); falling back to dotnet restore");
            if (Environment.GetEnvironmentVariable("BSHARP_RESTORE_DEBUG") == "1" && !string.IsNullOrWhiteSpace(output))
                Console.Error.WriteLine(output.TrimEnd());
        } else {
            Console.Error.WriteLine($"bsharp: cached bsharp restore done in {sw.ElapsedMilliseconds}ms");
        }
        return rc;
    }

    static bool HasDiscoverableTaskDaemon() {
        var env = Environment.GetEnvironmentVariable("BSHARP_TASKD_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return true;
        if (string.IsNullOrEmpty(Environment.ProcessPath))
            return false;

        var launcherDir = Path.GetDirectoryName(ResolveLauncherRealPath(Environment.ProcessPath));
        if (string.IsNullOrEmpty(launcherDir))
            return false;
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bsharp-taskd.exe" : "bsharp-taskd";
        return File.Exists(Path.Combine(launcherDir, exe));
    }

    static int RunBuildBinaryAndWait(string binFile, List<string> forwardArgs) {
        SetBuildBinaryEnvironment();
        var psi = new ProcessStartInfo(binFile) { UseShellExecute = false };
        foreach (var a in forwardArgs) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode;
    }

    static int RunBuildBinaryAndWaitQuiet(string binFile, List<string> forwardArgs, out string output) {
        SetBuildBinaryEnvironment();
        var psi = new ProcessStartInfo(binFile) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in forwardArgs) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        return proc.ExitCode;
    }


    static int RestoreProject(string projectPath, List<KeyValuePair<string, string>> globalProps, string reason) {
        Console.Error.WriteLine($"bsharp: {reason} with dotnet restore...");
        var args = new List<string> { "restore", projectPath, "--nologo", "-v:q" };
        foreach (var p in globalProps)
            args.Add($"-p:{p.Key}={p.Value}");
        var sw = Stopwatch.StartNew();
        var rc = RunProcess("dotnet", args);
        sw.Stop();
        if (rc != 0)
            Console.Error.WriteLine($"bsharp: restore failed (exit {rc})");
        else
            Console.Error.WriteLine($"bsharp: restore done in {sw.ElapsedMilliseconds}ms");
        return rc;
    }

    static int RunAudit(string projectPath, List<KeyValuePair<string, string>> globalProps) {
        var codegenTool = FindCodegen();
        if (codegenTool == null) {
            Console.Error.WriteLine("bsharp: cannot find codegen tool. Set BSHARP_CODEGEN env var to the path of Codegen.dll or a Codegen executable.");
            return 4;
        }

        var args = new List<string> { "--audit", "--project", projectPath };
        foreach (var p in globalProps) {
            args.Add("-p");
            args.Add($"{p.Key}={p.Value}");
        }

        return codegenTool.RequiresDotnet
            ? RunProcess("dotnet", new[] { codegenTool.Path }.Concat(args))
            : RunProcess(codegenTool.Path, args);
    }

    static void ApplyBsharpDefaultGlobalProperties(List<KeyValuePair<string, string>> props) {
        AddDefaultGlobalProp(props, "SuppressNETCoreSdkPreviewMessage", "true");
        AddDefaultGlobalProp(props, "EnableSourceControlManagerQueries", "false");
        AddDefaultGlobalProp(props, "EnableSourceLink", "false");
    }

    static void AddDefaultGlobalProp(List<KeyValuePair<string, string>> props, string name, string value) {
        if (!TryGetGlobalProp(props, name, out _))
            props.Add(new KeyValuePair<string, string>(name, value));
    }

    static void TryAddProp(List<KeyValuePair<string, string>> dest, string kv) {
        var eq = kv.IndexOf('=');
        if (eq <= 0) return; // ignore malformed
        dest.Add(new KeyValuePair<string, string>(kv.Substring(0, eq), kv.Substring(eq + 1)));
    }

    static bool TryAddTargetsFromArg(List<string> dest, string arg) {
        if (arg.StartsWith("-t:", StringComparison.Ordinal) || arg.StartsWith("/t:", StringComparison.Ordinal)) {
            AddTargets(dest, arg[3..]);
            return true;
        }
        if (arg.StartsWith("--target:", StringComparison.Ordinal)) {
            AddTargets(dest, arg["--target:".Length..]);
            return true;
        }
        if (arg.StartsWith("-target:", StringComparison.Ordinal)) {
            AddTargets(dest, arg["-target:".Length..]);
            return true;
        }
        return false;
    }

    static void AddTargets(List<string> dest, string targets) {
        foreach (var target in targets.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)) {
            var trimmed = target.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!dest.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                dest.Add(trimmed);
        }
    }

    static bool TryGetGlobalProp(List<KeyValuePair<string, string>> props, string name, out string value) {
        for (var i = props.Count - 1; i >= 0; i--) {
            if (string.Equals(props[i].Key, name, StringComparison.OrdinalIgnoreCase)) {
                value = props[i].Value;
                return true;
            }
        }
        value = "";
        return false;
    }

    sealed record TargetFrameworkInfo(string TargetFramework, string[] TargetFrameworks);

    static bool TryEvaluateTargetFrameworkInfo(string projectPath, List<KeyValuePair<string, string>> globalProps, out TargetFrameworkInfo info) {
        info = new TargetFrameworkInfo("", Array.Empty<string>());
        var args = new List<string> {
            "msbuild", projectPath, "-nologo",
            "-getProperty:TargetFramework",
            "-getProperty:TargetFrameworks",
        };
        foreach (var p in globalProps)
            args.Add($"-p:{p.Key}={p.Value}");

        var rc = RunProcessCapture("dotnet", args, out var stdout, out _);
        if (rc != 0) return false;

        try {
            using var doc = JsonDocument.Parse(stdout);
            var props = doc.RootElement.GetProperty("Properties");
            var targetFramework = props.TryGetProperty("TargetFramework", out var tf) ? tf.GetString() ?? "" : "";
            var targetFrameworksText = props.TryGetProperty("TargetFrameworks", out var tfs) ? tfs.GetString() ?? "" : "";
            var targetFrameworks = targetFrameworksText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            info = new TargetFrameworkInfo(targetFramework, targetFrameworks);
            return true;
        } catch {
            return false;
        }
    }

    static int RebuildOuter(
        string projectPath,
        string bsharpRoot,
        string currentHash,
        List<KeyValuePair<string, string>> globalProps,
        IReadOnlyList<string> requestedTargets,
        string[] targetFrameworks)
    {
        Console.Error.WriteLine($"bsharp: regenerating outer dispatcher for {Path.GetFileName(projectPath)} ({string.Join(", ", targetFrameworks)})...");
        Directory.CreateDirectory(bsharpRoot);

        var innerBuilds = new List<string>(targetFrameworks.Length);
        foreach (var targetFramework in targetFrameworks) {
            var innerProps = new List<KeyValuePair<string, string>>(globalProps.Count + 1);
            innerProps.AddRange(globalProps.Where(p => !string.Equals(p.Key, "TargetFramework", StringComparison.OrdinalIgnoreCase)));
            innerProps.Add(new KeyValuePair<string, string>("TargetFramework", targetFramework));
            innerProps.Sort((x, y) => string.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase));

            var innerDir = Path.Combine(bsharpRoot, "inner", SanitizePathSegment(targetFramework));
            var innerHash = ComputeShapeHash(projectPath, innerProps, requestedTargets);
            var innerHashFile = Path.Combine(innerDir, "shape.hash");
            var innerBin = Path.Combine(innerDir, "build");
            if (!File.Exists(innerHashFile)
                || !File.Exists(innerBin)
                || !string.Equals(File.ReadAllText(innerHashFile).Split('\n', 2)[0].Trim(), innerHash, StringComparison.Ordinal))
            {
                var rc = Rebuild(projectPath, innerDir, innerHash, innerProps, requestedTargets);
                if (rc != 0) return rc;
            }
            innerBuilds.Add(innerBin);
        }

        var outerBin = Path.Combine(bsharpRoot, "build");
        WriteOuterDispatcher(outerBin, innerBuilds);

        var hashFile = Path.Combine(bsharpRoot, "shape.hash");
        File.WriteAllText(hashFile, currentHash + "\nOuterDispatcher\n" + ShapeHashVersion + "\n");
        Console.Error.WriteLine($"bsharp: outer dispatcher ready at {outerBin}");
        return 0;
    }

    static void StartBackgroundRebuildIfNeeded(
        string projectPath,
        string projectCacheRoot,
        string bsharpDir,
        string currentHash,
        List<KeyValuePair<string, string>> globalProps,
        IReadOnlyList<string> requestedTargets)
    {
        Directory.CreateDirectory(bsharpDir);
        var lockFile = Path.Combine(bsharpDir, "background-rebuild.lock");
        var logFile = Path.Combine(bsharpDir, "background-rebuild.log");

        if (!TryReserveBackgroundRebuild(lockFile)) {
            Console.Error.WriteLine("bsharp: background build binary generation is already running; using dotnet fallback for this invocation.");
            return;
        }

        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self) || !File.Exists(self)) {
            Console.Error.WriteLine("bsharp: cannot locate launcher executable for background codegen; using dotnet fallback for this invocation.");
            TryDelete(lockFile);
            return;
        }

        var workerArgs = new List<string> {
            BackgroundRebuildCommand,
            "--project", projectPath,
            "--project-cache-root", projectCacheRoot,
            "--bsharp-dir", bsharpDir,
            "--shape-hash", currentHash,
            "--lock-file", lockFile,
        };
        foreach (var p in globalProps) {
            workerArgs.Add("-p");
            workerArgs.Add($"{p.Key}={p.Value}");
        }
        if (requestedTargets.Count > 0) {
            workerArgs.Add("--target");
            workerArgs.Add(string.Join(';', requestedTargets));
        }

        try {
            var proc = StartDetachedProcess(self, workerArgs, Path.GetDirectoryName(projectPath)!, logFile);
            File.WriteAllText(lockFile, $"{proc.Id}\n{DateTimeOffset.UtcNow:O}\n{projectPath}\n{currentHash}\n");
            Console.Error.WriteLine($"bsharp: started background build binary generation (pid {proc.Id}, log: {logFile}); using dotnet fallback for this invocation.");
        } catch (Exception ex) {
            Console.Error.WriteLine($"bsharp: failed to start background codegen ({ex.Message}); using dotnet fallback for this invocation.");
            TryDelete(lockFile);
        }
    }

    static bool TryReserveBackgroundRebuild(string lockFile) {
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);

        for (var attempt = 0; attempt < 2; attempt++) {
            if (File.Exists(lockFile)) {
                if (IsBackgroundRebuildActive(lockFile))
                    return false;
                TryDelete(lockFile);
            }

            try {
                using var stream = new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var text = Encoding.UTF8.GetBytes($"starting\n{DateTimeOffset.UtcNow:O}\n");
                stream.Write(text);
                return true;
            } catch (IOException) {
                if (attempt == 0)
                    continue;
                return false;
            }
        }

        return false;
    }

    static bool IsBackgroundRebuildActive(string lockFile) {
        try {
            var lines = File.ReadAllLines(lockFile);
            if (lines.Length > 0 && int.TryParse(lines[0], out var pid)) {
                try {
                    var process = Process.GetProcessById(pid);
                    return !process.HasExited;
                } catch {
                    return false;
                }
            }

            // Treat a freshly reserved lock as active so competing launchers do not
            // race while the reserving process starts the detached worker.
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(lockFile);
            return age < TimeSpan.FromMinutes(5);
        } catch {
            return false;
        }
    }

    static Process StartDetachedProcess(string fileName, IReadOnlyList<string> args, string workingDirectory, string logFile) {
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);

        if (!OperatingSystem.IsWindows()) {
            var command = "exec " + ShellQuote(fileName);
            foreach (var arg in args)
                command += " " + ShellQuote(arg);
            command += " >> " + ShellQuote(logFile) + " 2>&1";

            var psi = new ProcessStartInfo("/bin/sh") {
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
            return Process.Start(psi)!;
        }

        var windowsPsi = new ProcessStartInfo(fileName) {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            windowsPsi.ArgumentList.Add(arg);
        return Process.Start(windowsPsi)!;
    }

    static int RunDotnetFallback(string command, string projectPath, List<string> forwardArgs, List<KeyValuePair<string, string>> globalProps) {
        Console.Error.WriteLine($"bsharp: running dotnet {command} while the build binary is prepared in the background...");
        var args = new List<string>();
        if (string.Equals(command, "run", StringComparison.OrdinalIgnoreCase)) {
            args.Add("run");
            args.Add("--project");
            args.Add(projectPath);
        } else {
            args.Add("build");
            args.Add(projectPath);
        }

        foreach (var arg in FilterDotnetFallbackArgs(forwardArgs, projectPath))
            args.Add(arg);
        foreach (var p in globalProps)
            args.Add($"-p:{p.Key}={p.Value}");

        return RunProcess("dotnet", args);
    }

    static IEnumerable<string> FilterDotnetFallbackArgs(List<string> forwardArgs, string projectPath) {
        foreach (var arg in forwardArgs) {
            if (string.Equals(arg, "build", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "run", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--fast-noop", StringComparison.OrdinalIgnoreCase))
                continue;

            if (arg.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFullPath(arg), projectPath, StringComparison.OrdinalIgnoreCase))
                continue;

            yield return arg;
        }
    }

    static int RunBackgroundRebuild(string[] args) {
        string? projectPath = null;
        string? projectCacheRoot = null;
        string? bsharpDir = null;
        string? currentHash = null;
        string? lockFile = null;
        var globalProps = new List<KeyValuePair<string, string>>();
        var requestedTargets = new List<string>();

        for (var i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--project" when i + 1 < args.Length:
                    projectPath = args[++i];
                    break;
                case "--project-cache-root" when i + 1 < args.Length:
                    projectCacheRoot = args[++i];
                    break;
                case "--bsharp-dir" when i + 1 < args.Length:
                    bsharpDir = args[++i];
                    break;
                case "--shape-hash" when i + 1 < args.Length:
                    currentHash = args[++i];
                    break;
                case "--lock-file" when i + 1 < args.Length:
                    lockFile = args[++i];
                    break;
                case "--property" or "-p" when i + 1 < args.Length:
                    TryAddProp(globalProps, args[++i]);
                    break;
                case "-t" or "--target" or "-target" when i + 1 < args.Length:
                    AddTargets(requestedTargets, args[++i]);
                    break;
                default:
                    if (args[i].StartsWith("-p:", StringComparison.Ordinal) || args[i].StartsWith("/p:", StringComparison.Ordinal))
                        TryAddProp(globalProps, args[i][3..]);
                    else
                        TryAddTargetsFromArg(requestedTargets, args[i]);
                    break;
            }
        }
        ApplyBsharpDefaultGlobalProperties(globalProps);

        if (projectPath == null || projectCacheRoot == null || bsharpDir == null || currentHash == null || lockFile == null) {
            Console.Error.WriteLine("bsharp: background rebuild worker is missing required arguments.");
            return 2;
        }

        try {
            int rc;
            string hashFile;
            if (bsharpDir == projectCacheRoot
                && TryEvaluateTargetFrameworkInfo(projectPath, globalProps, out var frameworkInfo)
                && string.IsNullOrEmpty(frameworkInfo.TargetFramework)
                && frameworkInfo.TargetFrameworks.Length > 0)
            {
                rc = RebuildOuter(projectPath, projectCacheRoot, currentHash, globalProps, requestedTargets, frameworkInfo.TargetFrameworks);
                hashFile = Path.Combine(projectCacheRoot, "shape.hash");
            }
            else
            {
                rc = Rebuild(projectPath, bsharpDir, currentHash, globalProps, requestedTargets);
                hashFile = Path.Combine(bsharpDir, "shape.hash");
            }

            if (rc == 0 && File.Exists(hashFile))
                RefreshShapeHash(projectPath, globalProps, requestedTargets, hashFile);
            return rc;
        } finally {
            TryDelete(lockFile);
        }
    }

    static int Rebuild(string projectPath, string bsharpDir, string currentHash, List<KeyValuePair<string, string>> globalProps, IReadOnlyList<string> requestedTargets) {
        Console.Error.WriteLine($"bsharp: regenerating build binary for {Path.GetFileName(projectPath)}...");

        // SIMPLIFIED STRUCTURE: Generate directly into .bsharp/ instead of .bsharp/src/
        // Old: .bsharp/src/{Program.cs, TaskModel.cs, BsharpGenerated.csproj, task-server/, bin/, obj/}
        // New: .bsharp/{Program.cs, TaskModel.cs, BsharpGenerated.csproj, task-server/, bin/, obj/}
        // Benefits: Flatter, easier to inspect, fewer directories
        Directory.CreateDirectory(bsharpDir);
        var genDir = bsharpDir; // Generate directly into .bsharp/ root

        // Clean old generated files if they exist (but preserve shape.hash, build symlink)
        var filesToClean = new[] { "Program.cs", "TaskModel.cs", "DaemonPaths.cs", "BsharpGenerated.csproj", "tasks.report.txt" };
        foreach (var f in filesToClean) {
            var path = Path.Combine(genDir, f);
            if (File.Exists(path)) File.Delete(path);
        }
        // task-server/ and tasks/ are legacy from earlier per-project sidecar; codegen will
        // also scrub them but cleaning here guards against stale binaries from older runs.
        var dirsToClean = new[] { "task-server", "tasks", "bin", "obj", "src" };
        foreach (var d in dirsToClean) {
            var path = Path.Combine(genDir, d);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        var codegenTool = FindCodegen();
        if (codegenTool == null) {
            Console.Error.WriteLine("bsharp: cannot find codegen tool. Set BSHARP_CODEGEN env var to the path of Codegen.dll or a Codegen executable.");
            return 4;
        }
        Console.Error.WriteLine($"bsharp: codegen ({Path.GetFileName(codegenTool.Path)})...");
        var codegenSw = Stopwatch.StartNew();
        var codegenArgs = new List<string> { "--project", projectPath, "--out-dir", genDir };
        if (requestedTargets.Count > 0) {
            codegenArgs.Add("--targets");
            codegenArgs.Add(string.Join(';', requestedTargets));
        }
        foreach (var p in globalProps) {
            codegenArgs.Add("-p");
            codegenArgs.Add($"{p.Key}={p.Value}");
        }
        var codegenRc = codegenTool.RequiresDotnet
            ? RunProcess("dotnet", new[] { codegenTool.Path }.Concat(codegenArgs))
            : RunProcess(codegenTool.Path, codegenArgs);
        codegenSw.Stop();
        if (codegenRc != 0) {
            Console.Error.WriteLine($"bsharp: codegen failed (exit {codegenRc})");
            return 5;
        }
        Console.Error.WriteLine($"bsharp: codegen done in {codegenSw.ElapsedMilliseconds}ms");

        var generatedHostCsproj = Path.Combine(genDir, "BsharpGenerated.csproj");
        if (!File.Exists(generatedHostCsproj)) {
            Console.Error.WriteLine("bsharp: codegen output is missing BsharpGenerated.csproj.");
            Console.Error.WriteLine("bsharp: this usually means BSHARP_CODEGEN points to an older Codegen.dll/executable.");
            Console.Error.WriteLine("bsharp: rebuild tools/codegen and point BSHARP_CODEGEN at tools/codegen/bin/Debug/net11.0/Codegen or Codegen.dll.");
            return 5;
        }

        var rid = RuntimeInformation.RuntimeIdentifier;

        // The generated build host stays NativeAOT and self-contained. Real SDK tasks
        // are delegated to the universal `bsharp-taskd` daemon, which is installed
        // alongside the bsharp launcher (and discovered by the host via sibling path
        // / BSHARP_TASKD_PATH).
        var modes = new (string Label, string[] ExtraProps)[] {
            ("NativeAOTHost", new[] {
                "-p:PublishAot=true",
                "-p:PublishReadyToRun=false",
                "-p:SelfContained=true",
                "-p:PublishSingleFile=true",
                "-p:PublishTrimmed=true",
                "-p:TrimMode=full",
            }),
        };

        string? publishedBin = null;
        string usedMode = "";
        foreach (var (label, props) in modes) {
            Console.Error.WriteLine($"bsharp: publish ({rid}, {label})...");
            var publishSw = Stopwatch.StartNew();
            var args = new List<string> { "publish", genDir, "-c", "Release", "-r", rid, "--nologo", "-v:q" };
            args.AddRange(props);
            var rc = RunProcess("dotnet", args);
            publishSw.Stop();

            var candidate = Path.Combine(genDir, "bin", "Release", "net11.0", rid, "publish", ExecutableName("BsharpGenerated"));
            if (rc == 0 && File.Exists(candidate)) {
                publishedBin = candidate;
                usedMode = label;
                Console.Error.WriteLine($"bsharp: publish ({label}) done in {publishSw.ElapsedMilliseconds}ms");
                break;
            }
            Console.Error.WriteLine($"bsharp: publish ({label}) failed (exit {rc}); trying next mode");
            // Clean before next attempt so we don't pick up a stale binary.
            try {
                var dir = Path.GetDirectoryName(candidate);
                if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
            } catch { }
        }

        if (publishedBin == null) {
            Console.Error.WriteLine("bsharp: all publish modes failed");
            return 6;
        }

        var publishDir = Path.GetDirectoryName(publishedBin)!;

        // Single-file publishes keep the app and runtime in the apphost. Prefer a link, but
        // fall back to copying because Windows may disallow symlink creation.
        var binFile = Path.Combine(bsharpDir, "build");
        ReplaceFileLinkOrCopy(binFile, publishedBin);

        var hashFile = Path.Combine(bsharpDir, "shape.hash");
        var fullMode = $"{usedMode}+UniversalTaskDaemon";
        File.WriteAllText(hashFile, currentHash + "\n" + fullMode + "\n" + ShapeHashVersion + "\n");
        Console.Error.WriteLine($"bsharp: build binary ready (mode={fullMode}) at {binFile} -> {publishedBin}");
        return 0;
    }

    static int RunProcess(string fileName, IEnumerable<string> args) {
        var psi = new ProcessStartInfo(fileName) {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode;
    }

    static int RunProcessCapture(string fileName, IEnumerable<string> args, out string stdout, out string stderr) {
        var psi = new ProcessStartInfo(fileName) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        stdout = proc.StandardOutput.ReadToEnd();
        stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode;
    }

    static string ExecutableName(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;

    static void DeleteExistingFileOrDirectory(string path) {
        if (File.Exists(path) || new FileInfo(path).LinkTarget != null) {
            File.Delete(path);
            return;
        }
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    static void TryDelete(string path) {
        try {
            if (File.Exists(path))
                File.Delete(path);
        } catch {
            // A competing launcher/worker may already have consumed the lock.
        }
    }

    static void ReplaceFileLinkOrCopy(string destination, string source) {
        DeleteExistingFileOrDirectory(destination);
        try {
            File.CreateSymbolicLink(destination, source);
        } catch (Exception) when (OperatingSystem.IsWindows()) {
            File.Copy(source, destination, overwrite: true);
        }
    }

    static void WriteOuterDispatcher(string destination, IReadOnlyList<string> innerBuilds) {
        DeleteExistingFileOrDirectory(destination);
        if (OperatingSystem.IsWindows()) {
            var cmdPath = destination + ".cmd";
            DeleteExistingFileOrDirectory(cmdPath);
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal");
            foreach (var innerBuild in innerBuilds) {
                sb.Append("call ");
                sb.Append(CommandQuote(innerBuild));
                sb.AppendLine(" %*");
                sb.AppendLine("if errorlevel 1 exit /b %errorlevel%");
            }
            File.WriteAllText(cmdPath, sb.ToString());
            File.WriteAllText(destination, cmdPath);
            return;
        }

        var script = new StringBuilder();
        script.AppendLine("#!/bin/sh");
        script.AppendLine("rc=0");
        script.AppendLine("pids=''");
        foreach (var innerBuild in innerBuilds) {
            script.Append("exec_path=");
            script.Append(ShellQuote(innerBuild));
            script.AppendLine();
            script.AppendLine("\"$exec_path\" \"$@\" &");
            script.AppendLine("pids=\"$pids $!\"");
        }
        script.AppendLine("for pid in $pids; do");
        script.AppendLine("  if ! wait \"$pid\"; then rc=1; fi");
        script.AppendLine("done");
        script.AppendLine("exit \"$rc\"");
        File.WriteAllText(destination, script.ToString());
        try {
            File.SetUnixFileMode(destination,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        } catch { }
    }

    static string SanitizePathSegment(string value) {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value) {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_') sb.Append(ch);
            else sb.Append('_');
        }
        return sb.Length == 0 ? "default" : sb.ToString();
    }

    static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    static string CommandQuote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    static void ReplaceDirectoryLinkOrCopy(string destination, string sourceDirectory) {
        DeleteExistingFileOrDirectory(destination);
        try {
            Directory.CreateSymbolicLink(destination, sourceDirectory);
        } catch (Exception) when (OperatingSystem.IsWindows()) {
            CopyDirectory(sourceDirectory, destination);
        }
    }

    static void CopyDirectory(string source, string destination) {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    sealed record CodegenTool(string Path, bool RequiresDotnet);

    static CodegenTool? FindCodegen() {
        var envVar = Environment.GetEnvironmentVariable("BSHARP_CODEGEN");
        if (!string.IsNullOrEmpty(envVar) && File.Exists(envVar)) return ToCodegenTool(Path.GetFullPath(envVar));

        var self = Environment.ProcessPath;
        if (self == null) return null;
        var selfDir = Path.GetDirectoryName(self)!;
        // Common install layouts to probe
        string[] probes = {
            Path.Combine(selfDir, "Codegen.dll"),                                // Sibling (deployed layout)
            Path.Combine(selfDir, ExecutableName("Codegen")),                    // Sibling native
            Path.Combine(selfDir, "codegen", ExecutableName("Codegen")),
            Path.Combine(selfDir, "codegen", "Codegen.dll"),                     // /usr/share/bsharp/codegen/...
            Path.Combine(selfDir, "..", "codegen", ExecutableName("Codegen")),
            Path.Combine(selfDir, "..", "codegen", "Codegen.dll"),
            // Dev layout: launcher at tools/bsharp/bin/Release/net11.0/<rid>/publish/bsharp
            Path.Combine(selfDir, "..", "..", "..", "..", "..", "codegen", "bin", "Debug", "net11.0", ExecutableName("Codegen")),
            Path.Combine(selfDir, "..", "..", "..", "..", "..", "codegen", "bin", "Debug", "net11.0", "Codegen.dll"),
            Path.Combine(selfDir, "..", "..", "..", "..", "..", "codegen", "bin", "Release", "net11.0", ExecutableName("Codegen")),
            Path.Combine(selfDir, "..", "..", "..", "..", "..", "codegen", "bin", "Release", "net11.0", "Codegen.dll"),
        };
        foreach (var p in probes) {
            var full = Path.GetFullPath(p);
            if (File.Exists(full)) return ToCodegenTool(full);
        }
        return null;
    }

    static CodegenTool ToCodegenTool(string path) =>
        new(path, string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase));

    static string? ResolveProject(string? arg) {
        if (arg != null) {
            var abs = Path.GetFullPath(arg);
            return File.Exists(abs) ? abs : null;
        }
        var candidates = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.csproj").ToList();
        return candidates.Count == 1 ? candidates[0] : candidates.FirstOrDefault();
    }

    static string? ResolveSolution(string? arg) {
        if (arg != null) {
            var abs = Path.GetFullPath(arg);
            return File.Exists(abs) ? abs : null;
        }
        var candidates = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.sln").ToList();
        return candidates.Count == 1 ? candidates[0] : null; // Return null if multiple, to fall back to project mode
    }

    static int BuildSolution(string solutionPath, string command, string[] forwardArgs, List<KeyValuePair<string, string>> globalProps, bool noCache, bool backgroundCodegen) {
        if (command == "clean") {
            return CleanSolution(solutionPath);
        }
        
        if (command == "test") {
            return TestSolution(solutionPath, forwardArgs);
        }
        
        if (command != "build") {
            Console.Error.WriteLine($"bsharp: solution-level '{command}' not yet implemented; use 'build' only");
            return 2;
        }

        Solution solution;
        try {
            solution = SolutionParser.Parse(solutionPath);
        } catch (Exception ex) {
            Console.Error.WriteLine($"bsharp: failed to parse solution: {ex.Message}");
            return 2;
        }

        if (solution.Projects.Length == 0) {
            Console.Error.WriteLine($"bsharp: no C# projects found in solution");
            return 2;
        }

        // Build dependency graph
        var graph = DependencyGraph.Build(solution.Projects);
        var projectsByPath = solution.Projects.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"bsharp: building {solution.Projects.Length} project(s) in {graph.LayerCount} layer(s)");
        
        int failedCount = 0;
        var failedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int layerIndex = 0; layerIndex < graph.LayerCount; layerIndex++) {
            var layer = graph.GetLayer(layerIndex);
            if (layer.Length == 0) continue;

            if (graph.LayerCount > 1) {
                Console.WriteLine($"  Layer {layerIndex + 1}/{graph.LayerCount}: {layer.Length} project(s)");
            }

            // Build projects in this layer in parallel
            var buildTasks = new List<Task<(string Path, string Name, int ExitCode)>>();
            
            foreach (var projectPath in layer) {
                var proj = projectsByPath[projectPath];
                
                // Skip if any dependency failed
                var deps = graph.GetDependencies(projectPath);
                if (deps.Any(d => failedProjects.Contains(d))) {
                    Console.Error.WriteLine($"    ⊘ {proj.Name} skipped (dependency failed)");
                    failedProjects.Add(projectPath);
                    failedCount++;
                    continue;
                }

                var task = Task.Run(() => BuildProject(proj, command, forwardArgs, globalProps, noCache, backgroundCodegen));
                buildTasks.Add(task);
            }

            // Wait for all builds in this layer to complete
            Task.WaitAll(buildTasks.ToArray());

            foreach (var task in buildTasks) {
                var (path, name, rc) = task.Result;
                if (rc != 0) {
                    Console.Error.WriteLine($"    ✗ {name} failed (exit code {rc})");
                    failedProjects.Add(path);
                    failedCount++;
                } else {
                    Console.WriteLine($"    ✓ {name} succeeded");
                }
            }

            // Stop if any project in this layer failed
            if (failedProjects.Count > 0 && layerIndex < graph.LayerCount - 1) {
                Console.Error.WriteLine($"bsharp: stopping due to failures in layer {layerIndex + 1}");
                break;
            }
        }

        if (failedCount > 0) {
            Console.Error.WriteLine($"bsharp: {failedCount} project(s) failed");
            return 1;
        }

        Console.WriteLine($"bsharp: all {solution.Projects.Length} project(s) built successfully");
        return 0;
    }

    static (string Path, string Name, int ExitCode) BuildProject(SolutionProject proj, string command, string[] forwardArgs, List<KeyValuePair<string, string>> globalProps, bool noCache, bool backgroundCodegen) {
        var projectArgs = new List<string> { "build", proj.Path };
        // Forward relevant args but skip solution file and "build" command
        foreach (var arg in forwardArgs) {
            if (!arg.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && arg != "build")
                projectArgs.Add(arg);
        }
        foreach (var prop in globalProps)
            projectArgs.Add($"-p:{prop.Key}={prop.Value}");
        if (noCache)
            projectArgs.Add("--no-cache");
        if (backgroundCodegen)
            projectArgs.Add("--background-codegen");

        int rc = Run(projectArgs.ToArray());
        return (proj.Path, proj.Name, rc);
    }

    static int CleanProject(string projectPath) {
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        
        Console.WriteLine($"bsharp: cleaning {projectName}...");
        
        int deletedCount = 0;
        var dirsToDelete = new[] {
            Path.Combine(projectDir, "bin"),
            Path.Combine(projectDir, "obj"),
            Path.Combine(projectDir, ".bsharp")
        };

        foreach (var dir in dirsToDelete) {
            if (Directory.Exists(dir)) {
                try {
                    Directory.Delete(dir, recursive: true);
                    Console.WriteLine($"  Deleted {Path.GetFileName(dir)}/");
                    deletedCount++;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"  Failed to delete {dir}: {ex.Message}");
                }
            }
        }

        if (deletedCount == 0) {
            Console.WriteLine($"  Nothing to clean");
        } else {
            Console.WriteLine($"  Cleaned {deletedCount} director{(deletedCount == 1 ? "y" : "ies")}");
        }

        return 0;
    }

    static int CleanSolution(string solutionPath) {
        Solution solution;
        try {
            solution = SolutionParser.Parse(solutionPath);
        } catch (Exception ex) {
            Console.Error.WriteLine($"bsharp: failed to parse solution: {ex.Message}");
            return 2;
        }

        if (solution.Projects.Length == 0) {
            Console.Error.WriteLine($"bsharp: no C# projects found in solution");
            return 2;
        }

        Console.WriteLine($"bsharp: cleaning {solution.Projects.Length} project(s)");
        
        foreach (var proj in solution.Projects) {
            CleanProject(proj.Path);
        }

        Console.WriteLine($"bsharp: cleaned all {solution.Projects.Length} project(s)");
        return 0;
    }

    static int TestProject(string projectPath, string[] forwardArgs) {
        Console.WriteLine($"bsharp: running tests for {Path.GetFileNameWithoutExtension(projectPath)}...");
        
        var args = new List<string> { "test", projectPath, "--nologo" };
        // Forward test-specific args (filters, logger, etc.)
        foreach (var arg in forwardArgs) {
            if (!arg.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && 
                !arg.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && 
                arg != "test")
                args.Add(arg);
        }

        return RunProcess("dotnet", args);
    }

    static int TestSolution(string solutionPath, string[] forwardArgs) {
        Solution solution;
        try {
            solution = SolutionParser.Parse(solutionPath);
        } catch (Exception ex) {
            Console.Error.WriteLine($"bsharp: failed to parse solution: {ex.Message}");
            return 2;
        }

        if (solution.Projects.Length == 0) {
            Console.Error.WriteLine($"bsharp: no C# projects found in solution");
            return 2;
        }

        // Filter to test projects (heuristic: name contains "Test" or "Tests")
        var testProjects = solution.Projects
            .Where(p => p.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (testProjects.Length == 0) {
            Console.WriteLine("bsharp: no test projects found (projects with 'Test' in name)");
            return 0;
        }

        Console.WriteLine($"bsharp: running tests for {testProjects.Length} project(s)");
        
        int failedCount = 0;
        foreach (var proj in testProjects) {
            int rc = TestProject(proj.Path, forwardArgs);
            if (rc != 0) {
                failedCount++;
            }
        }

        if (failedCount > 0) {
            Console.Error.WriteLine($"bsharp: {failedCount} test project(s) failed");
            return 1;
        }

        Console.WriteLine($"bsharp: all test projects passed");
        return 0;
    }

    static string ComputeShapeHash(string projectPath, List<KeyValuePair<string, string>> globalProps, IReadOnlyList<string> requestedTargets) {
        var projectDir = Path.GetDirectoryName(projectPath)!;
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();

        void Feed(string label, ReadOnlySpan<byte> bytes) {
            var labelBytes = Encoding.UTF8.GetBytes($"\n--- {label} ---\n");
            ms.Write(labelBytes);
            ms.Write(bytes);
        }
        void FeedFile(string label, string path) {
            if (!File.Exists(path)) return;
            Feed(label + ":" + Path.GetFileName(path), File.ReadAllBytes(path));
        }

        Feed("bsharp-shape-version", Encoding.UTF8.GetBytes(ShapeHashVersion));

        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void FeedAncestorShapeFiles(string dir) {
            var d = new DirectoryInfo(dir);
            while (d != null) {
                FeedFile("dir-build-props",    Path.Combine(d.FullName, "Directory.Build.props"));
                FeedFile("dir-build-targets",  Path.Combine(d.FullName, "Directory.Build.targets"));
                FeedFile("dir-packages-props", Path.Combine(d.FullName, "Directory.Packages.props"));
                FeedFile("nuget-config",       Path.Combine(d.FullName, "NuGet.config"));
                FeedFile("global-json",        Path.Combine(d.FullName, "global.json"));
                d = d.Parent;
            }
        }

        void FeedProjectGraph(string path) {
            path = Path.GetFullPath(path);
            if (!visitedProjects.Add(path))
                return;

            FeedFile("project", path);
            var dir = Path.GetDirectoryName(path)!;
            FeedAncestorShapeFiles(dir);
            FeedFile("packages-lock", Path.Combine(dir, "packages.lock.json"));
            FeedFile("project-assets", Path.Combine(dir, "obj", "project.assets.json"));

            foreach (var importPath in EnumerateStaticMsBuildPaths(path, "Import", "Project"))
                FeedImportGraph(importPath);

            foreach (var referencePath in EnumerateStaticMsBuildPaths(path, "ProjectReference", "Include"))
                FeedProjectGraph(referencePath);
        }

        void FeedImportGraph(string path) {
            path = Path.GetFullPath(path);
            if (!visitedImports.Add(path))
                return;

            FeedFile("import", path);
            foreach (var importPath in EnumerateStaticMsBuildPaths(path, "Import", "Project"))
                FeedImportGraph(importPath);
            foreach (var referencePath in EnumerateStaticMsBuildPaths(path, "ProjectReference", "Include"))
                FeedProjectGraph(referencePath);
        }

        FeedProjectGraph(projectPath);

        // Global properties from -p:X=Y. Sorted by key (case-insensitive) so order is stable.
        foreach (var kv in globalProps)
            Feed("prop", Encoding.UTF8.GetBytes($"{kv.Key}={kv.Value}"));
        foreach (var target in requestedTargets)
            Feed("target", Encoding.UTF8.GetBytes(target));

        ms.Position = 0;
        var hash = sha.ComputeHash(ms);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static IEnumerable<string> EnumerateStaticMsBuildPaths(string filePath, string elementName, string attributeName) {
        // Fast bail: read the file once and check for the element marker. The vast
        // majority of SDK-style csprojs have no static <Import> / <ProjectReference>
        // (everything is implicit via the SDK), so we can skip the comparatively
        // expensive XDocument.Load + DOM walk entirely. This avoids ~5-10ms of
        // launcher overhead per warm fast-path check on the hot incremental path.
        byte[] bytes;
        try { bytes = File.ReadAllBytes(filePath); }
        catch { yield break; }
        if (!ContainsAsciiSequence(bytes, "<" + elementName))
            yield break;

        var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes), LoadOptions.None);
        var baseDir = Path.GetDirectoryName(filePath)!;
        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase))) {
            var value = element.Attribute(attributeName)?.Value;
            var resolved = ResolveStaticMsBuildPath(baseDir, value);
            if (resolved != null)
                yield return resolved;
        }
    }

    // Case-insensitive ASCII substring search over a byte buffer. MSBuild element
    // names are pure ASCII so we don't need a full string-comparison fallback.
    static bool ContainsAsciiSequence(byte[] haystack, string needle) {
        if (needle.Length == 0) return true;
        if (haystack.Length < needle.Length) return false;
        var first = (byte)char.ToLowerInvariant(needle[0]);
        for (int i = 0; i <= haystack.Length - needle.Length; i++) {
            var h = haystack[i];
            if ((h | 0x20) != first) continue;
            bool matched = true;
            for (int j = 1; j < needle.Length; j++) {
                var hh = haystack[i + j];
                var nn = (byte)char.ToLowerInvariant(needle[j]);
                if ((hh | 0x20) != nn) { matched = false; break; }
            }
            if (matched) return true;
        }
        return false;
    }

    static string? ResolveStaticMsBuildPath(string baseDir, string? value) {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Contains("$(", StringComparison.Ordinal) ||
            value.Contains("%(", StringComparison.Ordinal) ||
            value.IndexOfAny(['*', '?']) >= 0)
            return null;

        value = value.Replace('\\', Path.DirectorySeparatorChar);
        var path = Path.IsPathRooted(value)
            ? value
            : Path.Combine(baseDir, value);
        path = Path.GetFullPath(path);
        return File.Exists(path) ? path : null;
    }

    static int ExecBuildBinary(string binFile, List<string> forwardArgs) {
        SetBuildBinaryEnvironment();

        var psi = new ProcessStartInfo(binFile) { UseShellExecute = false };
        foreach (var a in forwardArgs) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode;
    }

    static void SetBuildBinaryEnvironment() {
        if (string.IsNullOrEmpty(Environment.ProcessPath)) return;
        SetEnvironmentVariableForExec("BSHARP_LAUNCHER_PATH", Environment.ProcessPath);
        // Daemon binary is staged alongside the launcher. The host looks up sibling
        // first, but BSHARP_TASKD_PATH lets us also support odd layouts (link to the
        // launcher from a different directory than the daemon publish, dev tree, ...).
        if (Environment.GetEnvironmentVariable("BSHARP_TASKD_PATH") is null) {
            var launcherDir = Path.GetDirectoryName(ResolveLauncherRealPath(Environment.ProcessPath));
            if (!string.IsNullOrEmpty(launcherDir)) {
                var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bsharp-taskd.exe" : "bsharp-taskd";
                var candidate = Path.Combine(launcherDir, exe);
                if (File.Exists(candidate))
                    SetEnvironmentVariableForExec("BSHARP_TASKD_PATH", candidate);
            }
        }
    }

    // CoreCLR's Environment.SetEnvironmentVariable on Unix does NOT update the
    // process's environ(7) array; it only updates a managed cache. That means
    // any env var we want child processes to inherit must go through libc
    // setenv(3) directly.
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    static extern int LibcSetenv(string name, string value, int overwrite);

    static void SetEnvironmentVariableForExec(string name, string value) {
        Environment.SetEnvironmentVariable(name, value);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            _ = LibcSetenv(name, value, 1);
        }
    }

    static string ResolveLauncherRealPath(string launcherPath) {
        try {
            var info = new FileInfo(launcherPath);
            if (!string.IsNullOrEmpty(info.LinkTarget))
                return Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(launcherPath)!);
        } catch { }
        return launcherPath;
    }

    static int ExecBuildBinaryAndRefreshShapeHash(string binFile, List<string> forwardArgs, string projectPath, List<KeyValuePair<string, string>> globalProps, IReadOnlyList<string> requestedTargets, string hashFile) {
        // On Unix we execv into the build binary, so any post-exec refresh
        // would never run. Bump the hash file's mtime *before* exec'ing so the
        // next call's stat-based fast path triggers. We don't recompute the
        // hash here: callers only invoke this on a cache hit, so the on-disk
        // hash is already correct -- we just need to tell the filesystem "yes,
        // this is current as of now".
        if (File.Exists(hashFile)) {
            try { File.SetLastWriteTimeUtc(hashFile, DateTime.UtcNow); } catch { }
        }
        return ExecBuildBinary(binFile, forwardArgs);
    }

    static void RefreshShapeHash(string projectPath, List<KeyValuePair<string, string>> globalProps, IReadOnlyList<string> requestedTargets, string hashFile) {
        var lines = File.ReadAllText(hashFile).Split('\n');
        var mode = lines.Length > 1 ? lines[1] : "";
        var hash = ComputeShapeHash(projectPath, globalProps, requestedTargets);
        File.WriteAllText(hashFile, hash + "\n" + mode + "\n" + ShapeHashVersion + "\n");
    }

    // Cheap stat-only check used as the launcher fast path. We trust the cached
    // shape.hash if none of the *user-controlled* shape inputs has a newer
    // mtime than the cache. This skips SHA256-ing every Directory.Build.* and
    // re-parsing every .csproj that ComputeShapeHash walks.
    //
    // Inputs we look at (project-local; SDK imports never change between builds):
    //   - The project file itself
    //   - All ancestor Directory.Build.props, Directory.Build.targets,
    //     Directory.Packages.props, NuGet.config, global.json (walk up to root)
    //   - packages.lock.json + obj/project.assets.json next to the project
    //   - Statically resolvable <Import Project="..."/> and <ProjectReference Include="..."/>
    //     entries, walked transitively with the same per-project shape inputs.
    //
    // If any of these is newer than the hashFile (or any disappeared / appeared),
    // we fall back to the full ComputeShapeHash + content check.
    static bool IsHashFileStillFresh(string projectPath, string hashFile) {
        if (!ShapeHashVersionMatches(hashFile))
            return false;

        var hashMtime = File.GetLastWriteTimeUtc(hashFile);
        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ProjectGraphStillFresh(projectPath, hashMtime, visitedProjects, visitedImports);

        static bool ProjectGraphStillFresh(string path, DateTime threshold, HashSet<string> visitedProjects, HashSet<string> visitedImports) {
            path = Path.GetFullPath(path);
            if (!visitedProjects.Add(path)) return true;
            if (NewerThan(path, threshold)) return false;

            var projDir = Path.GetDirectoryName(path)!;
            if (NewerThan(Path.Combine(projDir, "packages.lock.json"), threshold)) return false;
            if (NewerThan(Path.Combine(projDir, "obj", "project.assets.json"), threshold)) return false;

            var d = new DirectoryInfo(projDir);
            while (d != null) {
                if (NewerThan(Path.Combine(d.FullName, "Directory.Build.props"), threshold)) return false;
                if (NewerThan(Path.Combine(d.FullName, "Directory.Build.targets"), threshold)) return false;
                if (NewerThan(Path.Combine(d.FullName, "Directory.Packages.props"), threshold)) return false;
                if (NewerThan(Path.Combine(d.FullName, "NuGet.config"), threshold)) return false;
                if (NewerThan(Path.Combine(d.FullName, "global.json"), threshold)) return false;
                d = d.Parent;
            }

            foreach (var importPath in EnumerateStaticMsBuildPaths(path, "Import", "Project")) {
                if (!ImportGraphStillFresh(importPath, threshold, visitedProjects, visitedImports))
                    return false;
            }
            foreach (var referencePath in EnumerateStaticMsBuildPaths(path, "ProjectReference", "Include")) {
                if (!ProjectGraphStillFresh(referencePath, threshold, visitedProjects, visitedImports))
                    return false;
            }
            return true;
        }

        static bool ImportGraphStillFresh(string path, DateTime threshold, HashSet<string> visitedProjects, HashSet<string> visitedImports) {
            path = Path.GetFullPath(path);
            if (!visitedImports.Add(path)) return true;
            if (NewerThan(path, threshold)) return false;
            foreach (var importPath in EnumerateStaticMsBuildPaths(path, "Import", "Project")) {
                if (!ImportGraphStillFresh(importPath, threshold, visitedProjects, visitedImports))
                    return false;
            }
            foreach (var referencePath in EnumerateStaticMsBuildPaths(path, "ProjectReference", "Include")) {
                if (!ProjectGraphStillFresh(referencePath, threshold, visitedProjects, visitedImports))
                    return false;
            }
            return true;
        }

        static bool NewerThan(string path, DateTime threshold) {
            if (!File.Exists(path)) return false;
            return File.GetLastWriteTimeUtc(path) > threshold;
        }
    }

    static bool ShapeHashVersionMatches(string hashFile) {
        try {
            var lines = File.ReadAllText(hashFile).Split('\n');
            return lines.Length > 2 && string.Equals(lines[2].Trim(), ShapeHashVersion, StringComparison.Ordinal);
        } catch {
            return false;
        }
    }
}
