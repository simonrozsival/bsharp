using System.Text.Json;

namespace Bsharp.Tests;

internal sealed class CodegenUnitProject : IDisposable
{
    private CodegenUnitProject(TempProject project)
    {
        Project = project;
    }

    public TempProject Project { get; }
    public string DirectoryPath => Project.DirectoryPath;
    public string ProjectPath => Project.ProjectPath;

    public static CodegenUnitProject FromScenario(string caseId, string projectFileName)
    {
        var source = Path.Combine(BsharpTestEnvironment.RepoRoot, "fixtures", "msbuild-unit-scenarios", "cases", caseId);
        return new CodegenUnitProject(TempProject.CopyDirectoryTo(source, projectFileName, "unit-" + caseId));
    }

    public JsonDocument Audit(params string[] extraArguments)
    {
        var result = RunAudit(extraArguments);
        result.AssertSuccess("run codegen audit");
        return JsonDocument.Parse(result.StandardOutput);
    }

    public CommandResult RunAudit(params string[] extraArguments)
    {
        var arguments = new List<string> { "--audit", "--project", ProjectPath };
        arguments.AddRange(extraArguments);
        return RunCodegen(arguments);
    }

    public GeneratedCode Generate(string entryTarget = "Build", params string[] extraArguments)
    {
        var outputDirectory = Path.Combine(DirectoryPath, "generated");
        var arguments = new List<string> { "--project", ProjectPath, "--out-dir", outputDirectory, "--entry", entryTarget };
        arguments.AddRange(extraArguments);

        var result = RunCodegen(arguments);
        result.AssertSuccess("run codegen");
        return new GeneratedCode(outputDirectory, File.ReadAllText(Path.Combine(outputDirectory, "Program.cs")), result);
    }

    private CommandResult RunCodegen(IReadOnlyList<string> arguments) =>
        CommandRunner.Run(
            BsharpTestEnvironment.CodegenPath,
            arguments,
            DirectoryPath,
            BsharpTestEnvironment.DotnetEnvironment,
            BsharpTestEnvironment.CommandTimeout);

    public void Dispose() => Project.Dispose();
}

internal sealed record GeneratedCode(string OutputDirectory, string ProgramText, CommandResult Result);

internal static class GeneratedCodeExtensions
{
    public static void BuildHost(this GeneratedCode generated)
    {
        CommandRunner.Run(
                "dotnet",
                ["build", Path.Combine(generated.OutputDirectory, "BsharpGenerated.csproj"), "--nologo", "-v:q"],
                generated.OutputDirectory,
                BsharpTestEnvironment.DotnetEnvironment,
                BsharpTestEnvironment.CommandTimeout)
            .AssertSuccess("build generated host");
    }

    public static CommandResult RunHost(this GeneratedCode generated, params string[] arguments)
    {
        var runArguments = new List<string>
        {
            "run",
            "--project",
            Path.Combine(generated.OutputDirectory, "BsharpGenerated.csproj"),
            "--no-build",
            "--",
            "--no-restore",
            "-v:quiet"
        };
        runArguments.AddRange(arguments);

        return CommandRunner.Run(
            "dotnet",
            runArguments,
            generated.OutputDirectory,
            BsharpTestEnvironment.DotnetEnvironment,
            BsharpTestEnvironment.CommandTimeout);
    }
}
