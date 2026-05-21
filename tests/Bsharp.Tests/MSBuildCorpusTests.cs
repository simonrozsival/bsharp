using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bsharp.Tests;

[TestClass]
public sealed class MSBuildCorpusTests
{
    private const string OptInEnvVar = "BSHARP_RUN_MSBUILD_CORPUS_TESTS";
    private const string CaseFilterEnvVar = "BSHARP_MSBUILD_CORPUS_CASE";
    private const string CorpusFolder = "msbuild-e2e-corpus";
    private static readonly TimeSpan CorpusCommandTimeout = TimeSpan.FromMinutes(20);

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        if (!IsOptedIn) return;
        BsharpTestEnvironment.EnsureToolchain();
        var artifactsDir = ArtifactsDirectory();
        if (Directory.Exists(artifactsDir)) Directory.Delete(artifactsDir, recursive: true);
        Directory.CreateDirectory(artifactsDir);
    }

    [TestMethod]
    [TestCategory("MSBuildCorpus")]
    public void HeadToHead_SingleProject() => HeadToHead_DotnetVsBsharp("single-project");

    [TestMethod]
    [TestCategory("MSBuildCorpus")]
    public void HeadToHead_ProjectWithDependencies() => HeadToHead_DotnetVsBsharp("project-with-dependencies");

    [TestMethod]
    [TestCategory("MSBuildCorpus")]
    public void HeadToHead_NonSdkSingleProject() => HeadToHead_DotnetVsBsharp("non-sdk-single-project");

    [TestMethod]
    [TestCategory("MSBuildCorpus")]
    public void HeadToHead_NonSdkProjectWithDependencies() => HeadToHead_DotnetVsBsharp("non-sdk-project-with-dependencies");

    private void HeadToHead_DotnetVsBsharp(string caseId)
    {
        var caseFilter = Environment.GetEnvironmentVariable(CaseFilterEnvVar);
        if (!string.IsNullOrWhiteSpace(caseFilter) &&
            !string.Equals(caseId, caseFilter, StringComparison.OrdinalIgnoreCase))
        {
            TestContext.WriteLine($"Skipping {caseId}; {CaseFilterEnvVar}={caseFilter}.");
            return;
        }

        if (!IsOptedIn)
        {
            TestContext.WriteLine($"Skipping {caseId}. Set {OptInEnvVar}=1 to enable MSBuild corpus tests.");
            return;
        }

        var manifestCase = LoadCase(caseId);
        TestContext.WriteLine($"=== {manifestCase.Id} ({manifestCase.ExpectedBsharp}) ===");

        var report = new CorpusReport
        {
            CaseId = manifestCase.Id,
            UpstreamPath = manifestCase.UpstreamPath,
            ExpectedBsharp = manifestCase.ExpectedBsharp,
        };

        try
        {
            using var dotnetProject = TempProject.CopyDirectoryTo(
                Path.Combine(CorpusRoot(), manifestCase.SourceRoot),
                manifestCase.EntryProject,
                manifestCase.Id + "-dotnet");
            CorpusNormalizer.Apply(dotnetProject.DirectoryPath, manifestCase.Normalizations);

            var dotnetCold = TimedRun("dotnet", ["build", manifestCase.EntryProject, "-v:minimal", "--nologo"],
                dotnetProject.DirectoryPath, BsharpTestEnvironment.DotnetEnvironment);
            report.Dotnet.Cold = ToPhase(dotnetCold);

            if (!dotnetCold.Success)
            {
                report.Verdict = "environment-failure-dotnet";
                TestContext.WriteLine($"  dotnet cold build failed (exit {dotnetCold.Result.ExitCode}); skipping bsharp comparison.");
                WriteReport(manifestCase.Id, report);
                return;
            }

            var dotnetWarm = TimedRun("dotnet", ["build", manifestCase.EntryProject, "--no-restore", "-v:minimal", "--nologo"],
                dotnetProject.DirectoryPath, BsharpTestEnvironment.DotnetEnvironment);
            report.Dotnet.Warm = ToPhase(dotnetWarm);
            Assert.IsTrue(dotnetWarm.Success, $"Expected dotnet warm build to succeed for corpus case '{manifestCase.Id}'.");

            string? dotnetMarker = null;
            if (!string.IsNullOrEmpty(manifestCase.Mutation))
                CorpusMutator.Apply(dotnetProject.DirectoryPath, manifestCase, out dotnetMarker, out _);

            var dotnetInc = TimedRun("dotnet", ["run", "--project", manifestCase.EntryProject, "--no-restore", "--no-launch-profile"],
                dotnetProject.DirectoryPath, BsharpTestEnvironment.DotnetEnvironment);
            report.Dotnet.Incremental = ToPhase(dotnetInc);
            Assert.IsTrue(dotnetInc.Success, $"Expected dotnet incremental run to succeed for corpus case '{manifestCase.Id}'.");
            if (dotnetMarker is not null)
                StringAssert.Contains(dotnetInc.Result.StandardOutput, dotnetMarker,
                    $"Expected dotnet incremental output to include '{dotnetMarker}'.");

            using var bsharpProject = TempProject.CopyDirectoryTo(
                Path.Combine(CorpusRoot(), manifestCase.SourceRoot),
                manifestCase.EntryProject,
                manifestCase.Id + "-bsharp");
            CorpusNormalizer.Apply(bsharpProject.DirectoryPath, manifestCase.Normalizations);
            var bsharpEntryDirectory = Path.Combine(
                bsharpProject.DirectoryPath,
                Path.GetDirectoryName(manifestCase.EntryProject) ?? "");
            var bsharpEntryProject = Path.GetFileName(manifestCase.EntryProject);

            if (manifestCase.BsharpNoRestore)
            {
                var bsharpRestore = TimedRun("dotnet", ["restore", manifestCase.EntryProject, "--nologo", "-v:minimal"],
                    bsharpProject.DirectoryPath, BsharpTestEnvironment.DotnetEnvironment);
                Assert.IsTrue(bsharpRestore.Success,
                    $"Expected bsharp corpus pre-restore to succeed for '{manifestCase.Id}'.\nstderr:\n{bsharpRestore.Result.StandardError}\nstdout:\n{bsharpRestore.Result.StandardOutput}");
            }

            var bsharpColdArgs = new List<string> { "build", "--no-cache", "-v:quiet" };
            if (manifestCase.BsharpNoRestore)
                bsharpColdArgs.Add("--no-restore");
            bsharpColdArgs.Add(bsharpEntryProject);

            var bsharpCold = TimedRun(BsharpTestEnvironment.BsharpPath,
                bsharpColdArgs,
                bsharpEntryDirectory, BsharpTestEnvironment.BsharpEnvironment);
            report.Bsharp.Cold = ToPhase(bsharpCold);

            switch (manifestCase.ExpectedBsharp)
            {
                case "supported":
                    Assert.IsTrue(bsharpCold.Success,
                        $"Expected supported case '{manifestCase.Id}' to build under bsharp.\nstderr:\n{bsharpCold.Result.StandardError}\nstdout:\n{bsharpCold.Result.StandardOutput}");

                    var bsharpWarmArgs = new List<string> { "build", "-v:quiet" };
                    if (manifestCase.BsharpNoRestore)
                        bsharpWarmArgs.Add("--no-restore");
                    bsharpWarmArgs.Add(bsharpEntryProject);

                    var bsharpWarm = TimedRun(BsharpTestEnvironment.BsharpPath,
                        bsharpWarmArgs,
                        bsharpEntryDirectory, BsharpTestEnvironment.BsharpEnvironment);
                    report.Bsharp.Warm = ToPhase(bsharpWarm);
                    Assert.IsTrue(bsharpWarm.Success, "Expected warm bsharp build to succeed for supported case.");
                    Assert.IsFalse(bsharpWarm.Result.StandardError.Contains("regenerating", StringComparison.OrdinalIgnoreCase),
                        $"Warm bsharp build for '{manifestCase.Id}' unexpectedly regenerated:\n{bsharpWarm.Result.StandardError}");

                    string? marker = null;
                    if (!string.IsNullOrEmpty(manifestCase.Mutation))
                        CorpusMutator.Apply(bsharpProject.DirectoryPath, manifestCase, out marker, out _);

                    var bsharpInc = TimedRun(Path.Combine(bsharpEntryDirectory, ".bsharp", "build"),
                        ["--no-restore", "-v:quiet", "run"], bsharpEntryDirectory,
                        BsharpTestEnvironment.DotnetEnvironment);
                    report.Bsharp.Incremental = ToPhase(bsharpInc);
                    Assert.IsTrue(bsharpInc.Success, "Expected incremental bsharp run to succeed for supported case.");
                    if (marker is not null)
                        StringAssert.Contains(bsharpInc.Result.StandardOutput, marker,
                            $"Expected bsharp incremental output to include '{marker}'.");

                    report.Verdict = "match-supported";
                    break;

                case "known-unsupported":
                    if (bsharpCold.Success)
                    {
                        report.Verdict = "unexpected-success-bsharp";
                        Assert.Fail($"Case '{manifestCase.Id}' is marked known-unsupported but bsharp succeeded. Update corpus.json.");
                    }
                    report.Verdict = "matches-known-unsupported";
                    TestContext.WriteLine($"  bsharp surfaced expected failure ({manifestCase.UnsupportedReason}).");
                    break;

                default:
                    Assert.Fail($"Unknown expectedBsharp value '{manifestCase.ExpectedBsharp}' for case '{manifestCase.Id}'.");
                    break;
            }
        }
        catch (Exception ex) when (report.Verdict is null)
        {
            report.Verdict = "exception";
            report.Error = ex.Message;
            throw;
        }
        finally
        {
            WriteReport(manifestCase.Id, report);
        }
    }

    private static bool IsOptedIn =>
        string.Equals(Environment.GetEnvironmentVariable(OptInEnvVar), "1", StringComparison.Ordinal);

    private static string CorpusRoot() =>
        Path.Combine(BsharpTestEnvironment.RepoRoot, "fixtures", CorpusFolder);

    private static string ManifestPath() => Path.Combine(CorpusRoot(), "corpus.json");

    private static string ArtifactsDirectory() =>
        Path.Combine(BsharpTestEnvironment.RepoRoot, "artifacts", "msbuild-corpus-results");

    private static CorpusCase LoadCase(string caseId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ManifestPath()));
        foreach (var element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (element.GetProperty("id").GetString() == caseId)
                return CorpusCase.FromJson(element);
        }
        throw new InvalidOperationException($"Corpus case '{caseId}' not found.");
    }

    private static TimedResult TimedRun(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        var sw = Stopwatch.StartNew();
        var result = CommandRunner.Run(fileName, arguments, workingDirectory, environment, CorpusCommandTimeout);
        sw.Stop();
        return new TimedResult(result, sw.Elapsed);
    }

    private static CorpusPhaseReport ToPhase(TimedResult timed) => new()
    {
        ExitCode = timed.Result.ExitCode,
        ElapsedMilliseconds = (long)timed.Elapsed.TotalMilliseconds,
        StdoutTail = TailLines(timed.Result.StandardOutput, 20),
        StderrTail = TailLines(timed.Result.StandardError, 20),
    };

    private static string TailLines(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = Math.Max(0, lines.Length - max);
        return string.Join("\n", lines.AsSpan(start).ToArray());
    }

    private static void WriteReport(string caseId, CorpusReport report)
    {
        try
        {
            var path = Path.Combine(ArtifactsDirectory(), caseId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Reporting is best-effort and must never fail the run.
        }
    }
}

internal sealed record TimedResult(CommandResult Result, TimeSpan Elapsed)
{
    public bool Success => Result.ExitCode == 0;
}

internal sealed record CorpusCase(
    string Id,
    string SourceRoot,
    string UpstreamPath,
    string EntryProject,
    string Kind,
    string ExpectedBsharp,
    string? UnsupportedReason,
    IReadOnlyList<string> Normalizations,
    string? Mutation,
    bool BsharpNoRestore)
{
    public static CorpusCase FromJson(JsonElement element)
    {
        var normalizations = new List<string>();
        if (element.TryGetProperty("normalizations", out var n))
            foreach (var s in n.EnumerateArray())
                normalizations.Add(s.GetString()!);

        return new CorpusCase(
            element.GetProperty("id").GetString()!,
            element.GetProperty("sourceRoot").GetString()!,
            element.GetProperty("upstreamPath").GetString()!,
            element.GetProperty("entryProject").GetString()!,
            element.GetProperty("kind").GetString()!,
            element.GetProperty("expectedBsharp").GetString()!,
            element.TryGetProperty("unsupportedReason", out var u) ? u.GetString() : null,
            normalizations,
            element.TryGetProperty("mutation", out var m) ? m.GetString() : null,
            element.TryGetProperty("bsharpNoRestore", out var noRestore) && noRestore.GetBoolean());
    }
}

internal sealed class CorpusReport
{
    public string CaseId { get; set; } = "";
    public string UpstreamPath { get; set; } = "";
    public string ExpectedBsharp { get; set; } = "";
    public string? Verdict { get; set; }
    public string? Error { get; set; }
    public CorpusToolReport Dotnet { get; set; } = new();
    public CorpusToolReport Bsharp { get; set; } = new();
}

internal sealed class CorpusToolReport
{
    public CorpusPhaseReport? Cold { get; set; }
    public CorpusPhaseReport? Warm { get; set; }
    public CorpusPhaseReport? Incremental { get; set; }
}

internal sealed class CorpusPhaseReport
{
    public int ExitCode { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string StdoutTail { get; set; } = "";
    public string StderrTail { get; set; } = "";
}

internal static class CorpusNormalizer
{
    public static void Apply(string rootDirectory, IReadOnlyList<string> steps)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case "pin-sdk":
                    DeleteEmbeddedGlobalJson(rootDirectory);
                    break;
                case "retarget-net11":
                    RetargetNet11(rootDirectory);
                    break;
                case "convert-non-sdk-to-sdk":
                    ConvertNonSdkProjectsToSdk(rootDirectory);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown normalization step '{step}'.");
            }
        }
    }

    private static void DeleteEmbeddedGlobalJson(string root)
    {
        foreach (var globalJson in Directory.EnumerateFiles(root, "global.json", SearchOption.AllDirectories))
            File.Delete(globalJson);
    }

    private static void RetargetNet11(string root)
    {
        foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(csproj);
            var rewritten = text
                .Replace("<TargetFramework>net10.0</TargetFramework>", "<TargetFramework>net11.0</TargetFramework>")
                .Replace("<TargetFramework>net9.0</TargetFramework>", "<TargetFramework>net11.0</TargetFramework>")
                .Replace("<TargetFramework>net8.0</TargetFramework>", "<TargetFramework>net11.0</TargetFramework>");
            if (rewritten != text) File.WriteAllText(csproj, rewritten);
        }
    }

    private static readonly Regex OutputTypePattern = new(@"<OutputType>(?<value>[^<]+)</OutputType>", RegexOptions.IgnoreCase);
    private static readonly Regex ProjectReferencePattern = new(@"<ProjectReference\s+Include=""(?<value>[^""]+)""\s*/>", RegexOptions.IgnoreCase);
    private static readonly Regex CompileItemPattern = new(@"<Compile\s+Include=""(?<value>[^""]+)""\s*/>", RegexOptions.IgnoreCase);

    private static void ConvertNonSdkProjectsToSdk(string root)
    {
        foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(csproj);
            if (!text.Contains("ToolsVersion=\"Current\"", StringComparison.Ordinal) &&
                !text.Contains("schemas.microsoft.com/developer/msbuild/2003", StringComparison.Ordinal))
                continue;

            var outputType = OutputTypePattern.Match(text) is { Success: true } om ? om.Groups["value"].Value : "Library";
            var projectRefs = ProjectReferencePattern.Matches(text).Select(m => m.Groups["value"].Value).ToArray();
            var compileItems = CompileItemPattern.Matches(text).Select(m => m.Groups["value"].Value).ToArray();

            var sb = new StringBuilder();
            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine($"    <OutputType>{outputType}</OutputType>");
            sb.AppendLine("    <TargetFramework>net11.0</TargetFramework>");
            sb.AppendLine("    <LangVersion>latest</LangVersion>");
            sb.AppendLine("    <Nullable>annotations</Nullable>");
            sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
            sb.AppendLine("  </PropertyGroup>");
            if (compileItems.Length > 0)
            {
                sb.AppendLine("  <ItemGroup>");
                foreach (var item in compileItems)
                    sb.AppendLine($"    <Compile Include=\"{item}\" />");
                sb.AppendLine("  </ItemGroup>");
            }
            if (projectRefs.Length > 0)
            {
                sb.AppendLine("  <ItemGroup>");
                foreach (var reference in projectRefs)
                    sb.AppendLine($"    <ProjectReference Include=\"{reference}\" />");
                sb.AppendLine("  </ItemGroup>");
            }
            sb.AppendLine("</Project>");
            File.WriteAllText(csproj, sb.ToString());
        }
    }
}

internal static class CorpusMutator
{
    private static readonly Regex MainBodyPattern = new(
        @"static\s+void\s+Main\s*\(\s*string\s*\[\s*\]\s*args\s*\)\s*\{(?<body>[^{}]*?)\}",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static void Apply(string rootDirectory, CorpusCase manifestCase, out string marker, out string mutatedFile)
    {
        marker = $"bsharp-corpus-{manifestCase.Id}-{Guid.NewGuid():N}";
        var entryDir = Path.GetDirectoryName(Path.Combine(rootDirectory, manifestCase.EntryProject))!;
        var programPath = Path.Combine(entryDir, "Program.cs");
        if (!File.Exists(programPath))
            throw new InvalidOperationException($"Entry Program.cs not found for case '{manifestCase.Id}' at {programPath}.");

        var original = File.ReadAllText(programPath);
        var injection = $"            System.Console.WriteLine(\"{marker}\");";

        string mutated;
        if (MainBodyPattern.IsMatch(original))
        {
            mutated = MainBodyPattern.Replace(original, m =>
            {
                var body = m.Groups["body"].Value.TrimEnd();
                var prefix = m.Value.Substring(0, m.Value.LastIndexOf('}'));
                return prefix + "\n" + injection + "\n        }";
            }, 1);
        }
        else
        {
            // top-level statements
            mutated = original + "\nSystem.Console.WriteLine(\"" + marker + "\");\n";
        }

        File.WriteAllText(programPath, mutated);
        mutatedFile = programPath;
    }
}
