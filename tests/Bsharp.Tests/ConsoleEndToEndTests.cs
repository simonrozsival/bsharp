namespace Bsharp.Tests;

[TestClass]
public sealed class ConsoleEndToEndTests
{
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
        AssertBuiltAppRuns(project, "Hello, World!");

        var originalShapeHash = ReadShapeHash(project);
        var warm = RunBsharp(project, "build", "-v:quiet");
        warm.AssertSuccess("run a warm launcher cache-hit build");
        Assert.IsFalse(
            warm.StandardError.Contains("regenerating", StringComparison.OrdinalIgnoreCase),
            $"Warm cache-hit build unexpectedly regenerated:\n{warm.StandardError}");
        Assert.AreEqual(originalShapeHash, ReadShapeHash(project), "Warm cache hit should not change the shape hash.");

        RunGeneratedHost(project, "--no-restore", "-v:quiet", "build")
            .AssertSuccess("run the generated host warm build path");
        var directRun = RunGeneratedHost(project, "--no-restore", "-v:quiet", "run");
        directRun.AssertSuccess("run the generated host direct run path");
        StringAssert.Contains(directRun.StandardOutput, "Hello, World!");

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
        Assert.AreNotEqual(initialShapeHash, ReadShapeHash(project), "Changing Directory.Build.props should change the launcher shape hash.");
    }

    private static CommandResult RunBsharp(TempProject project, params string[] arguments) =>
        CommandRunner.Run(
            BsharpTestEnvironment.BsharpPath,
            arguments,
            project.DirectoryPath,
            BsharpTestEnvironment.BsharpEnvironment,
            BsharpTestEnvironment.CommandTimeout);

    private static CommandResult RunGeneratedHost(TempProject project, params string[] arguments) =>
        CommandRunner.Run(
            Path.Combine(project.DirectoryPath, ".bsharp", "build"),
            arguments,
            project.DirectoryPath,
            BsharpTestEnvironment.DotnetEnvironment,
            BsharpTestEnvironment.CommandTimeout);

    private static void AssertGeneratedHostExists(TempProject project)
    {
        Assert.IsTrue(File.Exists(Path.Combine(project.DirectoryPath, ".bsharp", "build")), "Expected .bsharp/build to exist.");
        Assert.IsTrue(File.Exists(Path.Combine(project.DirectoryPath, ".bsharp", "shape.hash")), "Expected .bsharp/shape.hash to exist.");
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
}
