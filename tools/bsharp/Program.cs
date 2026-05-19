// bsharp — the launcher. Native AOT.
// Locates the project, checks the .bsharp/ cache, codegens + publishes if stale,
// then execs the per-project compiled binary.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

return Launcher.Run(args);

static class Launcher {
    public static int Run(string[] args) {
        string command = "build";
        bool noCache = false;
        string? projectArg = null;
        var forwardArgs = new List<string>();
        var globalProps = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < args.Length; i++) {
            var a = args[i];
            switch (a) {
                case "build": case "run":
                    command = a;
                    forwardArgs.Add(a);
                    break;
                case "--no-cache":
                    noCache = true;
                    break;
                case "--project":
                    if (i + 1 < args.Length) projectArg = args[++i];
                    break;
                case "--property" or "-p":
                    if (i + 1 < args.Length) TryAddProp(globalProps, args[++i]);
                    break;
                default:
                    if (a.StartsWith("-p:", StringComparison.Ordinal) || a.StartsWith("/p:", StringComparison.Ordinal)) {
                        TryAddProp(globalProps, a.Substring(3));
                    } else if (a.StartsWith("--property:", StringComparison.Ordinal)) {
                        TryAddProp(globalProps, a.Substring("--property:".Length));
                    } else if (a.EndsWith(".csproj", StringComparison.Ordinal) && projectArg == null) {
                        projectArg = a;
                        forwardArgs.Add(a);
                    } else {
                        forwardArgs.Add(a);
                    }
                    break;
            }
        }
        // Sort for hash stability.
        globalProps.Sort((x, y) => string.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase));

        string? projectPath = ResolveProject(projectArg);
        if (projectPath == null) {
            Console.Error.WriteLine("bsharp: no .csproj found in current directory (and none specified via --project)");
            return 2;
        }

        string projectDir = Path.GetDirectoryName(projectPath)!;
        string bsharpDir = Path.Combine(projectDir, ".bsharp");
        string hashFile = Path.Combine(bsharpDir, "shape.hash");
        string binFile = Path.Combine(bsharpDir, "build");
        string currentHash = ComputeShapeHash(projectPath, globalProps);

        // Cache hit?
        if (!noCache && File.Exists(hashFile) && File.Exists(binFile)) {
            // shape.hash is "<hex>\n<mode>\n"; only the first line is the content hash.
            var cached = File.ReadAllText(hashFile).Split('\n', 2)[0].Trim();
            if (string.Equals(cached, currentHash, StringComparison.Ordinal)) {
                return ExecBuildBinary(binFile, forwardArgs);
            }
        }

        // Cache miss — regenerate.
        int rebuildRc = Rebuild(projectPath, bsharpDir, currentHash, globalProps);
        if (rebuildRc != 0) return rebuildRc;
        return ExecBuildBinary(binFile, forwardArgs);
    }

    static void TryAddProp(List<KeyValuePair<string, string>> dest, string kv) {
        var eq = kv.IndexOf('=');
        if (eq <= 0) return; // ignore malformed
        dest.Add(new KeyValuePair<string, string>(kv.Substring(0, eq), kv.Substring(eq + 1)));
    }

    static int Rebuild(string projectPath, string bsharpDir, string currentHash, List<KeyValuePair<string, string>> globalProps) {
        Console.Error.WriteLine($"bsharp: regenerating build binary for {Path.GetFileName(projectPath)}...");

        Directory.CreateDirectory(bsharpDir);
        var srcDir = Path.Combine(bsharpDir, "src");
        if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);
        Directory.CreateDirectory(srcDir);

        var codegenTool = FindCodegen();
        if (codegenTool == null) {
            Console.Error.WriteLine("bsharp: cannot find codegen tool. Set BSHARP_CODEGEN env var to the path of Codegen.dll or a Codegen executable.");
            return 4;
        }
        Console.Error.WriteLine($"bsharp: codegen ({Path.GetFileName(codegenTool.Path)})...");
        var codegenSw = Stopwatch.StartNew();
        var codegenArgs = new List<string> { "--project", projectPath, "--out-dir", srcDir };
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

        var taskServerProject = Path.Combine(srcDir, "task-server", "BsharpTaskServer.csproj");
        if (!File.Exists(taskServerProject)) {
            Console.Error.WriteLine("bsharp: codegen output is missing task-server/BsharpTaskServer.csproj.");
            Console.Error.WriteLine("bsharp: this usually means BSHARP_CODEGEN points to an older Codegen.dll/executable.");
            Console.Error.WriteLine("bsharp: rebuild tools/codegen and point BSHARP_CODEGEN at tools/codegen/bin/Debug/net11.0/Codegen or Codegen.dll.");
            return 5;
        }

        var rid = RuntimeInformation.RuntimeIdentifier;

        // The generated host always runs as NativeAOT and delegates real SDK tasks to
        // the persistent CoreCLR task server. Keeping one execution mode avoids rooting
        // the older in-proc reflection loader in generated binaries.
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
            var args = new List<string> { "publish", srcDir, "-c", "Release", "-r", rid, "--nologo", "-v:q" };
            args.AddRange(props);
            var rc = RunProcess("dotnet", args);
            publishSw.Stop();

            var candidate = Path.Combine(srcDir, "bin", "Release", "net11.0", rid, "publish", ExecutableName("BsharpGenerated"));
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
        var taskServerRootDst = Path.Combine(publishDir, "tasks");
        {
            var serverDst = Path.Combine(taskServerRootDst, "server");
            Directory.CreateDirectory(serverDst);
            Console.Error.WriteLine("bsharp: publishing persistent task server (CoreCLR R2R)...");
            var serverSw = Stopwatch.StartNew();
            var serverArgs = new List<string> {
                "publish", taskServerProject, "-c", "Release", "-r", rid, "-o", serverDst, "--nologo", "-v:q",
                "-p:PublishAot=false",
                "-p:PublishReadyToRun=true",
                "-p:SelfContained=false",
                "-p:NoWarn=CS8012%3BCS8602%3BIL2026",
            };
            var serverRc = RunProcess("dotnet", serverArgs);
            serverSw.Stop();
            if (serverRc != 0) {
                Console.Error.WriteLine($"bsharp: task server publish failed (exit {serverRc})");
                return 7;
            }
            Console.Error.WriteLine($"bsharp: task server publish done in {serverSw.ElapsedMilliseconds}ms");
        }

        // Single-file publishes keep the app and runtime in the apphost. Prefer a link, but
        // fall back to copying because Windows may disallow symlink creation.
        var binFile = Path.Combine(bsharpDir, "build");
        ReplaceFileLinkOrCopy(binFile, publishedBin);

        var hashFile = Path.Combine(bsharpDir, "shape.hash");
        var fullMode = $"{usedMode}+CoreCLRTaskServer";
        File.WriteAllText(hashFile, currentHash + "\n" + fullMode + "\n");
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

    static string ExecutableName(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;

    static void DeleteExistingFileOrDirectory(string path) {
        if (File.Exists(path) || new FileInfo(path).LinkTarget != null) {
            File.Delete(path);
            return;
        }
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    static void ReplaceFileLinkOrCopy(string destination, string source) {
        DeleteExistingFileOrDirectory(destination);
        try {
            File.CreateSymbolicLink(destination, source);
        } catch (Exception) when (OperatingSystem.IsWindows()) {
            File.Copy(source, destination, overwrite: true);
        }
    }

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
            Path.Combine(selfDir, "codegen", ExecutableName("Codegen")),
            Path.Combine(selfDir, "codegen", "Codegen.dll"),                 // /usr/share/bsharp/codegen/...
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

    static string ComputeShapeHash(string projectPath, List<KeyValuePair<string, string>> globalProps) {
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

        FeedFile("csproj", projectPath);

        var d = new DirectoryInfo(projectDir);
        while (d != null) {
            FeedFile("dir-build-props",    Path.Combine(d.FullName, "Directory.Build.props"));
            FeedFile("dir-build-targets",  Path.Combine(d.FullName, "Directory.Build.targets"));
            FeedFile("dir-packages-props", Path.Combine(d.FullName, "Directory.Packages.props"));
            FeedFile("global-json",        Path.Combine(d.FullName, "global.json"));
            d = d.Parent;
        }

        // Global properties from -p:X=Y. Sorted by key (case-insensitive) so order is stable.
        foreach (var kv in globalProps)
            Feed("prop", Encoding.UTF8.GetBytes($"{kv.Key}={kv.Value}"));

        ms.Position = 0;
        var hash = sha.ComputeHash(ms);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static int ExecBuildBinary(string binFile, List<string> forwardArgs) {
        var psi = new ProcessStartInfo(binFile) { UseShellExecute = false };
        foreach (var a in forwardArgs) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
