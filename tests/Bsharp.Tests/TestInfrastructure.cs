using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bsharp.Tests;

internal static class BsharpTestEnvironment
{
    private static readonly object ToolchainGate = new();
    private static bool s_codegenReady;
    private static bool s_taskDaemonReady;
    private static bool s_toolchainReady;

    public static string RepoRoot { get; } = FindRepoRoot();
    public static TimeSpan CommandTimeout { get; } = TimeSpan.FromMinutes(10);

    public static string CodegenPath =>
        Path.Combine(RepoRoot, "tools", "codegen", "bin", "Debug", "net11.0", ExecutableName("Codegen"));

    public static string BsharpPath =>
        Path.Combine(RepoRoot, "tools", "bsharp", "bin", "Release", "net11.0",
            RuntimeInformation.RuntimeIdentifier, "publish", ExecutableName("bsharp"));

    public static string BsharpTaskdPath =>
        Path.Combine(RepoRoot, "tools", "bsharp", "bin", "Release", "net11.0",
            RuntimeInformation.RuntimeIdentifier, "publish", ExecutableName("bsharp-taskd"));

    public static string BsharpTaskdPublishDirectory =>
        Path.Combine(RepoRoot, "tools", "bsharp-taskd", "bin", "Release", "net11.0",
            RuntimeInformation.RuntimeIdentifier, "publish");

    public static string BsharpTaskdPublishPath =>
        Path.Combine(BsharpTaskdPublishDirectory, ExecutableName("bsharp-taskd"));

    public static IReadOnlyDictionary<string, string> DotnetEnvironment { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        };

    public static IReadOnlyDictionary<string, string> BsharpEnvironment { get; } =
        new Dictionary<string, string>(DotnetEnvironment, StringComparer.Ordinal)
        {
            ["BSHARP_CODEGEN"] = CodegenPath,
        };

    public static void EnsureToolchain()
    {
        lock (ToolchainGate)
        {
            if (s_toolchainReady)
                return;

            EnsureCodegenCore();

            CommandRunner
                .Run("dotnet",
                    ["publish", Path.Combine(RepoRoot, "tools", "bsharp", "Bsharp.csproj"), "-c", "Release",
                        "-r", RuntimeInformation.RuntimeIdentifier, "--nologo", "-v:q"],
                    RepoRoot,
                    DotnetEnvironment,
                    CommandTimeout)
                .AssertSuccess("publish bsharp launcher");

            // The launcher publish wipes any previously staged daemon binaries. Re-publish
            // the universal task daemon and copy it (and its companions) next to the
            // launcher so generated hosts can spawn it without an explicit BSHARP_TASKD_PATH.
            // This mirrors what build.sh does for normal development workflows.
            PublishTaskDaemon();

            var launcherDir = Path.GetDirectoryName(BsharpPath)!;
            foreach (var file in Directory.EnumerateFiles(BsharpTaskdPublishDirectory))
            {
                File.Copy(file, Path.Combine(launcherDir, Path.GetFileName(file)), overwrite: true);
            }

            Assert.IsTrue(File.Exists(CodegenPath), $"Expected codegen executable at {CodegenPath}");
            Assert.IsTrue(File.Exists(BsharpPath), $"Expected bsharp launcher at {BsharpPath}");
            Assert.IsTrue(File.Exists(BsharpTaskdPath), $"Expected bsharp-taskd at {BsharpTaskdPath}");
            s_taskDaemonReady = true;
            s_toolchainReady = true;
        }
    }

    public static void EnsureCodegen()
    {
        lock (ToolchainGate)
        {
            EnsureCodegenCore();
        }
    }

    public static IReadOnlyDictionary<string, string> DotnetEnvironmentWithTaskDaemon()
    {
        EnsureTaskDaemon();
        return new Dictionary<string, string>(DotnetEnvironment, StringComparer.Ordinal)
        {
            ["BSHARP_TASKD_PATH"] = BsharpTaskdPublishPath,
        };
    }

    private static void EnsureTaskDaemon()
    {
        lock (ToolchainGate)
        {
            if (s_taskDaemonReady)
                return;

            PublishTaskDaemon();

            Assert.IsTrue(File.Exists(BsharpTaskdPublishPath), $"Expected bsharp-taskd at {BsharpTaskdPublishPath}");
            s_taskDaemonReady = true;
        }
    }

    private static void PublishTaskDaemon() =>
        CommandRunner
            .Run("dotnet",
                ["publish", Path.Combine(RepoRoot, "tools", "bsharp-taskd", "BsharpTaskd.csproj"),
                    "-c", "Release", "-r", RuntimeInformation.RuntimeIdentifier,
                    "--no-self-contained", "-p:PublishReadyToRun=true", "--nologo", "-v:q"],
                RepoRoot,
                DotnetEnvironment,
                CommandTimeout)
            .AssertSuccess("publish bsharp-taskd daemon");

    private static void EnsureCodegenCore()
    {
        if (s_codegenReady)
            return;

        CommandRunner
            .Run("dotnet",
                ["build", Path.Combine(RepoRoot, "tools", "codegen", "Codegen.csproj"), "-c", "Debug", "--nologo", "-v:q"],
                RepoRoot,
                DotnetEnvironment,
                CommandTimeout)
            .AssertSuccess("build codegen");

        Assert.IsTrue(File.Exists(CodegenPath), $"Expected codegen executable at {CodegenPath}");
        s_codegenReady = true;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "README.md")) &&
                File.Exists(Path.Combine(dir.FullName, "tools", "bsharp", "Bsharp.csproj")) &&
                File.Exists(Path.Combine(dir.FullName, "tools", "codegen", "Codegen.csproj")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        Assert.Fail("Could not locate repository root from test output directory.");
        throw new UnreachableException();
    }

    private static string ExecutableName(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
}

internal sealed record CommandResult(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public void AssertSuccess(string context)
    {
        if (ExitCode == 0)
            return;

        Assert.Fail($"""
            Command failed while trying to {context}.

            Command:
            {FileName} {string.Join(" ", Arguments.Select(Quote))}

            Working directory:
            {WorkingDirectory}

            Exit code:
            {ExitCode}

            stdout:
            {TrimForFailure(StandardOutput)}

            stderr:
            {TrimForFailure(StandardError)}
            """);
    }

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;

    private static string TrimForFailure(string text)
    {
        const int maxLength = 12_000;
        return text.Length <= maxLength ? text : text[..maxLength] + "\n... <truncated>";
    }
}

internal static class CommandRunner
{
    public static CommandResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        if (environment is not null)
        {
            foreach (var pair in environment)
                psi.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(psi);
        Assert.IsNotNull(process, $"Failed to start command: {fileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var timeoutMilliseconds = checked((int)(timeout ?? TimeSpan.FromMinutes(2)).TotalMilliseconds);

        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited between timeout detection and Kill.
            }

            Assert.Fail($"Timed out running command: {fileName} {string.Join(" ", arguments)}");
        }

        return new CommandResult(
            fileName,
            arguments,
            workingDirectory,
            process.ExitCode,
            stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult());
    }
}

internal sealed class TempProject : IDisposable
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bsharp",
        "bin",
        "obj",
    };

    private TempProject(string cleanupRootPath, string directoryPath, string projectFileName)
    {
        CleanupRootPath = cleanupRootPath;
        DirectoryPath = directoryPath;
        ProjectFileName = projectFileName;
    }

    private string CleanupRootPath { get; }
    public string DirectoryPath { get; }
    public string ProjectFileName { get; }
    public string ProjectPath => Path.Combine(DirectoryPath, ProjectFileName);

    public static TempProject CopyFixture(string fixtureName, string projectFileName)
    {
        var source = Path.Combine(BsharpTestEnvironment.RepoRoot, "fixtures", fixtureName);
        Assert.IsTrue(Directory.Exists(source), $"Fixture directory does not exist: {source}");

        var cleanupRoot = Path.Combine(Path.GetTempPath(), "bsharp-tests", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(cleanupRoot, fixtureName);
        Directory.CreateDirectory(destination);
        CopyDirectory(source, destination);
        return new TempProject(cleanupRoot, destination, projectFileName);
    }

    public static TempProject CopyDirectoryTo(string sourceDirectory, string projectFileName, string? label = null)
    {
        Assert.IsTrue(Directory.Exists(sourceDirectory), $"Source directory does not exist: {sourceDirectory}");

        var cleanupRoot = Path.Combine(Path.GetTempPath(), "bsharp-tests", Guid.NewGuid().ToString("N"));
        var destination = string.IsNullOrEmpty(label)
            ? Path.Combine(cleanupRoot, Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? "case")
            : Path.Combine(cleanupRoot, label);
        Directory.CreateDirectory(destination);
        CopyDirectory(sourceDirectory, destination);
        return new TempProject(cleanupRoot, destination, projectFileName);
    }

    public static TempProject CreateEmpty(string projectFileName)
    {
        var destination = Path.Combine(Path.GetTempPath(), "bsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        return new TempProject(destination, destination, projectFileName);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(CleanupRootPath))
                Directory.Delete(CleanupRootPath, recursive: true);
        }
        catch
        {
            // Temporary test artifacts are best-effort cleanup.
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            var target = Path.Combine(destination, name);

            if (Directory.Exists(entry))
            {
                if (ExcludedDirectories.Contains(name))
                    continue;

                Directory.CreateDirectory(target);
                CopyDirectory(entry, target);
            }
            else
            {
                File.Copy(entry, target);
            }
        }
    }
}
