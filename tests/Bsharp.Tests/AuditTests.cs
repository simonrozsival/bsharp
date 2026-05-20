using System.Text.Json;

namespace Bsharp.Tests;

[TestClass]
public sealed class AuditTests
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        BsharpTestEnvironment.EnsureToolchain();
    }

    [TestMethod]
    public void ConsoleAuditReportsExpectedShape()
    {
        var projectPath = Path.Combine(BsharpTestEnvironment.RepoRoot, "fixtures", "console-net11", "console-net11.csproj");
        using var document = RunAudit(projectPath);
        var root = document.RootElement;

        Assert.AreEqual("net11.0", root.GetProperty("shape").GetProperty("targetFramework").GetString());
        Assert.IsFalse(root.GetProperty("shape").GetProperty("isOuterBuild").GetBoolean());

        var counts = root.GetProperty("counts");
        Assert.IsTrue(counts.GetProperty("targets").GetInt32() > 100, "Expected the console SDK audit to include the evaluated SDK target graph.");
        Assert.IsTrue(counts.GetProperty("cscTasks").GetInt32() > 0, "Expected Csc task sites in the console build graph.");
        Assert.AreEqual(0, counts.GetProperty("inlineUsingTasks").GetInt32(), "The console fixture should not use inline tasks.");
        Assert.AreEqual(0, counts.GetProperty("unresolvedUsingTasks").GetInt32(), "The console fixture should have no unresolved UsingTask entries.");
    }

    [TestMethod]
    public void AuditFlagsUnsupportedSyntheticShapeFeatures()
    {
        using var project = TempProject.CreateEmpty("UnsupportedAudit.csproj");
        File.WriteAllText(
            project.ProjectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <UsingTask TaskName="InlineHello"
                         TaskFactory="RoslynCodeTaskFactory"
                         AssemblyFile="$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll">
                <ParameterGroup />
                <Task>
                  <Code Type="Fragment" Language="cs"><![CDATA[
                    return true;
                  ]]></Code>
                </Task>
              </UsingTask>

              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <Batched Include="input.txt" />
              </ItemGroup>

              <Target Name="Build" Condition="'$([System.String]::Copy(`x`))' == 'x'">
                <Message Text="%(Batched.Identity)" />
                <InlineHello />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(project.DirectoryPath, "input.txt"), "input");

        using var document = RunAudit(project.ProjectPath);
        var counts = document.RootElement.GetProperty("counts");

        Assert.IsTrue(counts.GetProperty("inlineUsingTasks").GetInt32() > 0, "Expected audit to flag inline UsingTask entries.");
        Assert.IsTrue(counts.GetProperty("taskBatchingSites").GetInt32() > 0, "Expected audit to flag task batching sites.");
        Assert.IsTrue(counts.GetProperty("propertyFunctionSites").GetInt32() > 0, "Expected audit to flag property-function sites.");
    }

    [TestMethod]
    [TestCategory("Maui")]
    public void MauiAndroidAuditReportsStressShapeWhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BSHARP_RUN_MAUI_AUDIT_TESTS"), "1", StringComparison.Ordinal))
        {
            TestContext.WriteLine("Skipping MAUI audit. Set BSHARP_RUN_MAUI_AUDIT_TESTS=1 to enable this gated test.");
            return;
        }

        var projectPath = Path.Combine(BsharpTestEnvironment.RepoRoot, "fixtures", "maui-net11", "MauiNet11.csproj");
        if (!File.Exists(projectPath))
        {
            TestContext.WriteLine($"Skipping MAUI audit because the fixture is missing: {projectPath}");
            return;
        }

        var result = RunCodegenAudit(projectPath, "-p", "TargetFramework=net11.0-android");
        if (result.ExitCode != 0)
        {
            TestContext.WriteLine("Skipping MAUI audit because this environment cannot evaluate the MAUI workload.");
            TestContext.WriteLine(result.StandardOutput);
            TestContext.WriteLine(result.StandardError);
            return;
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.AreEqual("net11.0-android", root.GetProperty("shape").GetProperty("targetFramework").GetString());
        Assert.AreEqual("true", root.GetProperty("shape").GetProperty("useMaui").GetString());

        var counts = root.GetProperty("counts");
        Assert.IsTrue(counts.GetProperty("targets").GetInt32() >= 500, "Expected MAUI Android audit to expose a large target graph.");
        Assert.IsTrue(counts.GetProperty("usedTaskNames").GetInt32() >= 100, "Expected MAUI Android audit to expose many task names.");
        Assert.IsTrue(counts.GetProperty("unresolvedUsingTasks").GetInt32() > 0, "Expected current MAUI audit to retain unresolved UsingTask pressure.");
    }

    private static JsonDocument RunAudit(string projectPath, params string[] extraArguments)
    {
        var result = RunCodegenAudit(projectPath, extraArguments);
        result.AssertSuccess("run codegen audit");
        return JsonDocument.Parse(result.StandardOutput);
    }

    private static CommandResult RunCodegenAudit(string projectPath, params string[] extraArguments)
    {
        var arguments = new List<string> { "--audit", "--project", projectPath };
        arguments.AddRange(extraArguments);
        return CommandRunner.Run(
            BsharpTestEnvironment.CodegenPath,
            arguments,
            Path.GetDirectoryName(projectPath) ?? BsharpTestEnvironment.RepoRoot,
            BsharpTestEnvironment.DotnetEnvironment,
            BsharpTestEnvironment.CommandTimeout);
    }
}
