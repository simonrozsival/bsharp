namespace Bsharp.Tests;

[TestClass]
public sealed class ConsoleEndToEndTests
{
    private const string ConsoleFixtureOutput = "Hello 9";

    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        BsharpTestEnvironment.EnsureToolchain();
    }

    [TestMethod]
    public void ConsoleBuildLifecycleValidatesColdWarmDirectAndIncrementalPaths()
    {
        using var project = TempProject.CopyFixture("console-net11", "console-net11.csproj");

        var cold = RunBsharp(project, "build", "-v:quiet");
        cold.AssertSuccess("run a cold launcher build");
        StringAssert.Contains(cold.StandardError, "regenerating build binary");
        AssertGeneratedHostExists(project);
        AssertGeneratedTaskServerUsesDirectExecution(project);
        AssertBuiltAppRuns(project, ConsoleFixtureOutput);

        var originalShapeHash = ReadShapeHash(project);
        var warm = RunBsharp(project, "build", "-v:quiet");
        warm.AssertSuccess("run a warm launcher cache-hit build");
        Assert.IsFalse(
            warm.StandardError.Contains("regenerating", StringComparison.OrdinalIgnoreCase),
            $"Warm cache-hit build unexpectedly regenerated:\n{warm.StandardError}");
        Assert.AreEqual(originalShapeHash, ReadShapeHash(project), "Warm cache hit should not change the shape hash.");

        Directory.Delete(Path.Combine(project.DirectoryPath, "bin"), recursive: true);
        Directory.Delete(Path.Combine(project.DirectoryPath, "obj"), recursive: true);
        var runAfterOutputDelete = RunBsharp(project, "run", "-v:quiet");
        runAfterOutputDelete.AssertSuccess("restore/rebuild/run after bin and obj are deleted");
        Assert.IsFalse(
            runAfterOutputDelete.StandardError.Contains("regenerating", StringComparison.OrdinalIgnoreCase),
            $"Deleting bin/obj should not regenerate the build host when restored dependency assets are unchanged:\n{runAfterOutputDelete.StandardError}");
        StringAssert.Contains(runAfterOutputDelete.StandardError, "restoring missing project assets with cached bsharp host");
        Assert.IsFalse(
            runAfterOutputDelete.StandardError.Contains("restoring missing project assets before cache check with dotnet restore", StringComparison.OrdinalIgnoreCase),
            $"Expected cached bsharp restore, not dotnet restore, when the generated host is available:\n{runAfterOutputDelete.StandardError}");
        StringAssert.Contains(runAfterOutputDelete.StandardOutput, ConsoleFixtureOutput);
        Assert.AreEqual(originalShapeHash, ReadShapeHash(project), "Restoring identical dependency assets should preserve the shape hash.");

        RunGeneratedHost(project, "--no-restore", "-v:quiet", "build")
            .AssertSuccess("run the generated host warm build path");
        var directRun = RunGeneratedHost(project, "--no-restore", "-v:quiet", "run");
        directRun.AssertSuccess("run the generated host direct run path");
        StringAssert.Contains(directRun.StandardOutput, ConsoleFixtureOutput);

        var program = Path.Combine(project.DirectoryPath, "Program.cs");
        File.WriteAllText(program, "Console.WriteLine(\"Hello from incremental build!\");" + Environment.NewLine);
        File.SetLastWriteTimeUtc(program, DateTime.UtcNow.AddSeconds(2));

        var incrementalRun = RunGeneratedHost(project, "--no-restore", "-v:quiet", "run");
        incrementalRun.AssertSuccess("rebuild and run after a source edit");
        StringAssert.Contains(incrementalRun.StandardOutput, "Hello from incremental build!");
    }

    [TestMethod]
    public void ConsoleBuildRegeneratesForNoCacheAndShapeInputChanges()
    {
        using var project = TempProject.CopyFixture("console-net11", "console-net11.csproj");

        RunBsharp(project, "build", "-v:quiet")
            .AssertSuccess("run the initial launcher build");
        var initialShapeHash = ReadShapeHash(project);

        var forced = RunBsharp(project, "build", "--no-cache", "-v:quiet");
        forced.AssertSuccess("force regeneration with --no-cache");
        StringAssert.Contains(forced.StandardError, "regenerating build binary");
        Assert.AreEqual(initialShapeHash, ReadShapeHash(project), "--no-cache should regenerate the same shape when inputs are unchanged.");

        File.WriteAllText(
            Path.Combine(project.DirectoryPath, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <DefineConstants>$(DefineConstants);BSHARP_SHAPE_TEST</DefineConstants>
              </PropertyGroup>
            </Project>
            """);

        var shapeChanged = RunBsharp(project, "build", "-v:quiet");
        shapeChanged.AssertSuccess("regenerate after a shape input changes");
        StringAssert.Contains(shapeChanged.StandardError, "regenerating build binary");
        var directoryBuildShapeHash = ReadShapeHash(project);
        Assert.AreNotEqual(initialShapeHash, directoryBuildShapeHash, "Changing Directory.Build.props should change the launcher shape hash.");

        var assetsFile = Path.Combine(project.DirectoryPath, "obj", "project.assets.json");
        Assert.IsTrue(File.Exists(assetsFile), "Expected restore to produce project.assets.json.");
        File.AppendAllText(assetsFile, Environment.NewLine);

        var assetsChanged = RunBsharp(project, "build", "-v:quiet");
        assetsChanged.AssertSuccess("regenerate after project.assets.json changes");
        StringAssert.Contains(assetsChanged.StandardError, "regenerating build binary");
        Assert.AreNotEqual(directoryBuildShapeHash, ReadShapeHash(project), "Changing project.assets.json should change the launcher shape hash.");
    }

    [TestMethod]
    public void ConsoleBuildUsesSeparateCacheVariantsForGlobalProperties()
    {
        using var project = TempProject.CopyFixture("console-net11", "console-net11.csproj");

        RunBsharp(project, "build", "-v:quiet")
            .AssertSuccess("run the default launcher build");
        var defaultHash = ReadShapeHash(project);

        var release = RunBsharp(project, "build", "-p:Configuration=Release", "-v:quiet");
        release.AssertSuccess("run the Release global-property variant");
        StringAssert.Contains(release.StandardError, "regenerating build binary");

        var variantBuilds = Directory.GetFiles(
            Path.Combine(project.DirectoryPath, ".bsharp", "variants"),
            "build",
            SearchOption.AllDirectories);
        Assert.AreEqual(1, variantBuilds.Length, "Expected one non-TargetFramework global-property cache variant.");
        var releaseHash = File.ReadAllText(Path.Combine(Path.GetDirectoryName(variantBuilds[0])!, "shape.hash")).Split('\n', 2)[0].Trim();
        Assert.AreNotEqual(defaultHash, releaseHash, "Global-property variants should have distinct shape hashes.");

        var releaseWarm = RunBsharp(project, "build", "-p:Configuration=Release", "-v:quiet");
        releaseWarm.AssertSuccess("run the Release global-property cache hit");
        Assert.IsFalse(
            releaseWarm.StandardError.Contains("regenerating", StringComparison.OrdinalIgnoreCase),
            $"Release variant cache-hit unexpectedly regenerated:\n{releaseWarm.StandardError}");

        var defaultWarm = RunBsharp(project, "build", "-v:quiet");
        defaultWarm.AssertSuccess("run the default cache hit after Release variant");
        Assert.IsFalse(
            defaultWarm.StandardError.Contains("regenerating", StringComparison.OrdinalIgnoreCase),
            $"Default cache-hit unexpectedly regenerated after Release variant:\n{defaultWarm.StandardError}");
        Assert.AreEqual(defaultHash, ReadShapeHash(project), "Default cache hash should be isolated from global-property variants.");
    }

    [TestMethod]
    public void FastNoOpIsExplicitAndDoesNotPersistAcrossInvocations()
    {
        using var project = TempProject.CopyFixture("console-net11", "console-net11.csproj");

        RunBsharp(project, "build", "--no-restore", "-v:quiet")
            .AssertSuccess("create the generated host");
        StringAssert.Contains(
            ReadGeneratedProgramText(project),
            "if (I.PackageReference.Count != 0)",
            "Projects without PackageReference items should not pay task-server cost for CheckForImplicitPackageReferenceOverrides.");

        var fullGraph = RunBsharp(project, "run", "--no-restore", "-v:n");
        fullGraph.AssertSuccess("run without the fast no-op shortcut");
        AssertCumulativeTasksGreaterThanZero(fullGraph, "full graph should execute SDK tasks when --fast-noop is omitted");
        StringAssert.Contains(fullGraph.StandardOutput, ConsoleFixtureOutput);

        var fastNoOp = RunBsharp(project, "run", "--no-restore", "--fast-noop", "-v:n");
        fastNoOp.AssertSuccess("run with explicit --fast-noop");
        AssertCumulativeTasksEqualToZero(fastNoOp, "--fast-noop should return before target execution on an up-to-date build");
        StringAssert.Contains(fastNoOp.StandardOutput, ConsoleFixtureOutput);

        var afterFastNoOp = RunBsharp(project, "run", "--no-restore", "-v:n");
        afterFastNoOp.AssertSuccess("run again without --fast-noop");
        AssertCumulativeTasksGreaterThanZero(afterFastNoOp, "--fast-noop must not persist across invocations");
        StringAssert.Contains(afterFastNoOp.StandardOutput, ConsoleFixtureOutput);
    }

    [TestMethod]
    public void BackgroundCodegenFallsBackToDotnetThenUsesGeneratedHost()
    {
        using var project = TempProject.CopyFixture("console-net11", "console-net11.csproj");
        var backgroundEnvironment = new Dictionary<string, string>(BsharpTestEnvironment.BsharpEnvironment, StringComparer.Ordinal)
        {
            ["BSHARP_BACKGROUND_CODEGEN"] = "1",
        };

        var cold = RunBsharp(project, backgroundEnvironment, "build", "-v:quiet");
        cold.AssertSuccess("fall back to dotnet while background codegen starts");
        StringAssert.Contains(cold.StandardError, "started background build binary generation");
        StringAssert.Contains(cold.StandardError, "running dotnet build while the build binary is prepared in the background");
        AssertBuiltAppRuns(project, ConsoleFixtureOutput);

        WaitForGeneratedHost(project);

        var warm = RunBsharp(project, backgroundEnvironment, "build", "-v:quiet");
        warm.AssertSuccess("use the generated host after background codegen completes");
        Assert.IsFalse(
            warm.StandardError.Contains("running dotnet build while the build binary is prepared", StringComparison.OrdinalIgnoreCase),
            $"Expected completed background codegen to switch to the generated host:\n{warm.StandardError}");
        Assert.IsFalse(
            warm.StandardError.Contains("regenerating", StringComparison.OrdinalIgnoreCase),
            $"Expected completed background codegen to avoid synchronous regeneration:\n{warm.StandardError}");
    }

    [TestMethod]
    public void ProjectReferenceConsoleBuildsAndRuns()
    {
        using var project = TempProject.CopyDirectoryTo(
            Path.Combine(BsharpTestEnvironment.RepoRoot, "fixtures", "projectref-console-net11"),
            Path.Combine("App", "App.csproj"),
            "projectref-console-net11");

        var appDirectory = Path.Combine(project.DirectoryPath, "App");

        var cold = RunBsharpInDirectory(appDirectory, "build", "App.csproj", "-v:quiet");
        cold.AssertSuccess("run ProjectReference console launcher build");
        StringAssert.Contains(cold.StandardError, "restoring ProjectReference graph");
        StringAssert.Contains(cold.StandardError, "regenerating build binary");

        var builtDll = Path.Combine(project.DirectoryPath, "App", "bin", "Debug", "net11.0", "App.dll");
        Assert.IsTrue(File.Exists(builtDll), $"Expected built app at {builtDll}");
        Assert.IsTrue(File.Exists(Path.Combine(project.DirectoryPath, "Lib", "bin", "Debug", "net11.0", "Lib.dll")),
            "Expected referenced library output to be built.");
        Assert.IsTrue(File.Exists(Path.Combine(project.DirectoryPath, "Lib", ".bsharp", "build")),
            "Expected referenced library to be built through its bsharp host.");

        var run = CommandRunner.Run(
            "dotnet",
            ["exec", builtDll],
            Path.Combine(project.DirectoryPath, "App"),
            BsharpTestEnvironment.DotnetEnvironment,
            BsharpTestEnvironment.CommandTimeout);
        run.AssertSuccess("run ProjectReference console app");
        StringAssert.Contains(run.StandardOutput, "Hello from referenced library!");

        var warm = RunBsharpInDirectory(appDirectory, "build", "App.csproj", "-v:quiet");
        warm.AssertSuccess("run ProjectReference console warm build");
        Assert.IsFalse(
            warm.StandardError.Contains("regenerating", StringComparison.OrdinalIgnoreCase),
            $"ProjectReference warm cache-hit unexpectedly regenerated:\n{warm.StandardError}");

        var greeterPath = Path.Combine(project.DirectoryPath, "Lib", "Greeter.cs");
        File.WriteAllText(
            greeterPath,
            """
            namespace Lib;

            public static class Greeter
            {
                public static string Message => "Hello after referenced library edit!";
            }
            """ + Environment.NewLine);
        File.SetLastWriteTimeUtc(greeterPath, DateTime.UtcNow.AddSeconds(2));

        RunGeneratedHostInDirectory(appDirectory, "--no-restore", "-v:quiet", "build")
            .AssertSuccess("build ProjectReference generated host after referenced source edit");
        var incremental = CommandRunner.Run(
            "dotnet",
            ["exec", builtDll],
            appDirectory,
            BsharpTestEnvironment.DotnetEnvironment,
            BsharpTestEnvironment.CommandTimeout);
        incremental.AssertSuccess("run ProjectReference console app after referenced source edit");
        StringAssert.Contains(incremental.StandardOutput, "Hello after referenced library edit!");

        File.WriteAllText(
            Path.Combine(project.DirectoryPath, "Lib", "Lib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <BsharpShapeMarker>true</BsharpShapeMarker>
              </PropertyGroup>
            </Project>
            """ + Environment.NewLine);

        var shapeChanged = RunBsharpInDirectory(appDirectory, "build", "App.csproj", "-v:quiet");
        shapeChanged.AssertSuccess("regenerate ProjectReference console build after referenced shape edit");
        StringAssert.Contains(shapeChanged.StandardError, "regenerating build binary");
    }

    private static CommandResult RunBsharp(TempProject project, params string[] arguments) =>
        RunBsharp(project, BsharpTestEnvironment.BsharpEnvironment, arguments);

    private static CommandResult RunBsharp(TempProject project, IReadOnlyDictionary<string, string> environment, params string[] arguments) =>
        CommandRunner.Run(
            BsharpTestEnvironment.BsharpPath,
            arguments,
            project.DirectoryPath,
            environment,
            BsharpTestEnvironment.CommandTimeout);

    private static CommandResult RunBsharpInDirectory(string directoryPath, params string[] arguments) =>
        CommandRunner.Run(
            BsharpTestEnvironment.BsharpPath,
            arguments,
            directoryPath,
            BsharpTestEnvironment.BsharpEnvironment,
            BsharpTestEnvironment.CommandTimeout);

    private static CommandResult RunGeneratedHost(TempProject project, params string[] arguments) =>
        CommandRunner.Run(
            Path.Combine(project.DirectoryPath, ".bsharp", "build"),
            arguments,
            project.DirectoryPath,
            BsharpTestEnvironment.DotnetEnvironment,
            BsharpTestEnvironment.CommandTimeout);

    private static CommandResult RunGeneratedHostInDirectory(string directoryPath, params string[] arguments) =>
        CommandRunner.Run(
            Path.Combine(directoryPath, ".bsharp", "build"),
            arguments,
            directoryPath,
            BsharpTestEnvironment.DotnetEnvironment,
            BsharpTestEnvironment.CommandTimeout);

    private static void AssertGeneratedHostExists(TempProject project)
    {
        Assert.IsTrue(File.Exists(Path.Combine(project.DirectoryPath, ".bsharp", "build")), "Expected .bsharp/build to exist.");
        Assert.IsTrue(File.Exists(Path.Combine(project.DirectoryPath, ".bsharp", "shape.hash")), "Expected .bsharp/shape.hash to exist.");
    }

    private static void WaitForGeneratedHost(TempProject project)
    {
        var buildPath = Path.Combine(project.DirectoryPath, ".bsharp", "build");
        var hashPath = Path.Combine(project.DirectoryPath, ".bsharp", "shape.hash");
        var lockPath = Path.Combine(project.DirectoryPath, ".bsharp", "background-rebuild.lock");
        var logPath = Path.Combine(project.DirectoryPath, ".bsharp", "background-rebuild.log");
        var deadline = DateTime.UtcNow + BsharpTestEnvironment.CommandTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(buildPath) && File.Exists(hashPath) && !File.Exists(lockPath))
                return;

            Thread.Sleep(TimeSpan.FromSeconds(1));
        }

        var log = File.Exists(logPath) ? File.ReadAllText(logPath) : "<missing background-rebuild.log>";
        Assert.Fail($"Timed out waiting for background codegen to publish .bsharp/build.\n\nBackground log:\n{log}");
    }

    private static void AssertBuiltAppRuns(TempProject project, string expectedOutput)
    {
        var builtDll = Path.Combine(project.DirectoryPath, "bin", "Debug", "net11.0", "console-net11.dll");
        Assert.IsTrue(File.Exists(builtDll), $"Expected built app at {builtDll}");

        var result = CommandRunner.Run(
            "dotnet",
            ["exec", builtDll],
            project.DirectoryPath,
            BsharpTestEnvironment.DotnetEnvironment,
            BsharpTestEnvironment.CommandTimeout);
        result.AssertSuccess("run the built console app");
        StringAssert.Contains(result.StandardOutput, expectedOutput);
    }

    private static string ReadShapeHash(TempProject project) =>
        File.ReadAllText(Path.Combine(project.DirectoryPath, ".bsharp", "shape.hash"));

    private static string ReadGeneratedProgramText(TempProject project)
    {
        var candidates = Directory.GetFiles(Path.Combine(project.DirectoryPath, ".bsharp"), "Program.cs", SearchOption.AllDirectories);
        Assert.IsTrue(candidates.Length > 0, "Expected generated Program.cs under .bsharp.");
        return File.ReadAllText(candidates.OrderBy(path => path, StringComparer.Ordinal).First());
    }

    private static string ReadGeneratedTaskServerText(TempProject project)
    {
        var path = Path.Combine(project.DirectoryPath, ".bsharp", "task-server", "Program.cs");
        Assert.IsTrue(File.Exists(path), "Expected generated task-server/Program.cs under .bsharp.");
        return File.ReadAllText(path);
    }

    private static void AssertGeneratedTaskServerUsesDirectExecution(TempProject project)
    {
        var text = ReadGeneratedTaskServerText(project);
        StringAssert.Contains(text, "extern alias taskasm");
        StringAssert.Contains(text, "static TaskResult ExecuteTask");
        StringAssert.Contains(text, "new taskasm");
        StringAssert.Contains(text, "::Microsoft.CodeAnalysis.BuildTasks.Csc");
        StringAssert.Contains(text, "TaskServer.PrepareTask(task);");
        StringAssert.Contains(text, "var success = task.Execute();");

        Assert.IsFalse(text.Contains("Activator.CreateInstance", StringComparison.Ordinal), "Task server should instantiate task types directly.");
        Assert.IsFalse(text.Contains("execute.Invoke", StringComparison.Ordinal), "Task server should call task.Execute() directly.");
        Assert.IsFalse(text.Contains("MethodInfo", StringComparison.Ordinal), "Task server should not reflectively find Execute().");
        Assert.IsFalse(text.Contains("GetProperty(", StringComparison.Ordinal), "Task server should not reflectively get task properties.");
        Assert.IsFalse(text.Contains("using System.Reflection;", StringComparison.Ordinal), "Task server should not import reflection.");
    }

    private static void AssertCumulativeTasksEqualToZero(CommandResult result, string message) =>
        Assert.AreEqual(0, ReadCumulativeTasksMilliseconds(result), message + "\n\nstdout:\n" + result.StandardOutput);

    private static void AssertCumulativeTasksGreaterThanZero(CommandResult result, string message) =>
        Assert.IsTrue(ReadCumulativeTasksMilliseconds(result) > 0, message + "\n\nstdout:\n" + result.StandardOutput);

    private static double ReadCumulativeTasksMilliseconds(CommandResult result)
    {
        const string prefix = "cumulative tasks:";
        var line = result.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(line, "Expected generated host output to contain cumulative task timing.\n\nstdout:\n" + result.StandardOutput);

        var valueText = line[prefix.Length..].Trim();
        Assert.IsTrue(valueText.EndsWith("ms", StringComparison.OrdinalIgnoreCase), "Unexpected cumulative task timing format: " + line);
        valueText = valueText[..^2].Trim();
        Assert.IsTrue(double.TryParse(valueText, System.Globalization.CultureInfo.InvariantCulture, out var value), "Could not parse cumulative task timing: " + line);
        return value;
    }
}
