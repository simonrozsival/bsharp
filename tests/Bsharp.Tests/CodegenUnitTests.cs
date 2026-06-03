using System.Text.Json;

namespace Bsharp.Tests;

[TestClass]
public sealed class CodegenUnitTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => BsharpTestEnvironment.EnsureCodegen();

    [TestMethod]
    public void LiteralDependsOnTargetsPreservesPropertyMutationFlow()
    {
        using var project = CodegenUnitProject.FromScenario("target-depends-property-flow", "target-depends-property-flow.proj");
        var generated = project.Generate();

        StringAssert.Contains(generated.ProgramText, "P.Value = \"prepared\";");
        AssertInOrder(
            generated.ProgramText,
            "public static async ValueTask T_002_Build()",
            "await T_001_Prepare();",
            "Log.TargetStarted(\"Build\")",
            "P.Value + \" done\"");
        Assert.IsFalse(generated.ProgramText.Contains("public static async ValueTask Run(string name)", StringComparison.Ordinal),
            "Literal DependsOnTargets should not force the dynamic target dispatcher.");
    }

    [TestMethod]
    public void TargetConditionMatchesMSBuildOrdering()
    {
        using var project = CodegenUnitProject.FromScenario("target-condition-order", "target-condition-order.proj");
        var generated = project.Generate();

        AssertInOrder(
            generated.ProgramText,
            "public static async ValueTask T_003_Maybe()",
            "if (!(string.Equals(P.RunMaybe, \"true\", StringComparison.OrdinalIgnoreCase)))",
            "await T_002_BeforeMaybe();",
            "Log.TargetSkipped(\"Maybe\", \"condition was false\");",
            "await T_006_AfterMaybe();");
        StringAssert.Contains(generated.ProgramText, "await T_001_MaybeDependency();");
        StringAssert.Contains(generated.ProgramText, "await T_002_BeforeMaybe();");

        AssertInOrder(
            generated.ProgramText,
            "public static async ValueTask T_005_Build()",
            "await T_003_Maybe();",
            "await T_004_Flip();",
            "await T_003_Maybe();");

        StringAssert.Contains(generated.ProgramText, "TargetRuntime.MarkSkipped(\"Maybe\"");
    }

    [TestMethod]
    public void LargeLiteralDependencyBatchRunsInOrder()
    {
        using var project = CodegenUnitProject.FromScenario("target-depends-large-batch", "target-depends-large-batch.proj");
        var generated = project.Generate();

        AssertInOrder(
            generated.ProgramText,
            "public static async ValueTask T_006_Build()",
            "await T_001_A();",
            "await T_002_B();",
            "await T_003_C();",
            "await T_004_D();",
            "await T_005_E();");
        Assert.IsFalse(generated.ProgramText.Contains(".AsTask()", StringComparison.Ordinal),
            "Literal dependency execution should stay ValueTask-based.");
    }

    [TestMethod]
    public void InitialDefaultAndExplicitTargetsShapeGeneratedBuildPlan()
    {
        using var project = CodegenUnitProject.FromScenario("target-entry-order", "target-entry-order.proj");

        var defaultGenerated = project.Generate(entryTarget: null);
        StringAssert.Contains(defaultGenerated.Result.StandardOutput, "Initial targets: InitA;InitB");
        StringAssert.Contains(defaultGenerated.Result.StandardOutput, "Default targets: DefaultA;DefaultB");
        StringAssert.Contains(defaultGenerated.ProgramText, "requestedTargets is { Count: > 0 }");
        StringAssert.Contains(defaultGenerated.ProgramText, "await RunTargetOrError(\"InitA\");");
        StringAssert.Contains(defaultGenerated.ProgramText, "await RunTargetOrError(\"DefaultB\");");
        Assert.IsFalse(defaultGenerated.ProgramText.Contains("ExplicitA", StringComparison.Ordinal),
            "Default generation should not include unrelated explicit-only targets.");

        var explicitGenerated = project.Generate(entryTarget: null, "--targets", "ExplicitA;ExplicitB");
        StringAssert.Contains(explicitGenerated.Result.StandardOutput, "Requested targets: ExplicitA;ExplicitB");
        StringAssert.Contains(explicitGenerated.ProgramText, "await RunTargetOrError(\"InitA\");");
        StringAssert.Contains(explicitGenerated.ProgramText, "await RunTargetOrError(\"InitB\");");
        StringAssert.Contains(explicitGenerated.ProgramText, "foreach (var target in requestedTargets)");
        StringAssert.Contains(explicitGenerated.ProgramText, "string.Equals(name, \"ExplicitA\", StringComparison.OrdinalIgnoreCase)");
        StringAssert.Contains(explicitGenerated.ProgramText, "string.Equals(name, \"ExplicitB\", StringComparison.OrdinalIgnoreCase)");
    }

    [TestMethod]
    public void FastNoOpIsAutomaticByDefault()
    {
        using var project = CodegenUnitProject.FromScenario("target-entry-order", "target-entry-order.proj");
        var generated = project.Generate(entryTarget: null);

        StringAssert.Contains(generated.ProgramText, "bool noFastNoop = false;");
        StringAssert.Contains(generated.ProgramText, "else if (a == \"--no-fast-noop\") noFastNoop = true;");
        StringAssert.Contains(generated.ProgramText, "var allowFastNoOp = command != \"restore\"");
        StringAssert.Contains(generated.ProgramText, "&& !noBuild");
        StringAssert.Contains(generated.ProgramText, "&& !noFastNoop");
        StringAssert.Contains(generated.ProgramText, "&& requestedTargets.Count == 0;");
        StringAssert.Contains(generated.ProgramText, "var fastNoOp = preInitFastNoOp || (allowFastNoOp && FastNoOpBuild(csprojPath));");
    }

    [TestMethod]
    public void MultiItemIncludePreservesMetadataForTransforms()
    {
        using var project = CodegenUnitProject.FromScenario("multi-item-include-metadata", "multi-item-include-metadata.proj");
        var generated = project.Generate();

        AssertInOrder(
            generated.ProgramText,
            "foreach (var sourceItem in I.A)",
            "sourceItem.CopyMetadataTo(newItem);",
            "I.Combined.Add(newItem);",
            "foreach (var sourceItem in I.B)",
            "sourceItem.CopyMetadataTo(newItem);",
            "I.Combined.Add(newItem);");
        StringAssert.Contains(generated.ProgramText, "I.Combined.Select(transformItem => transformItem.GetMetadata(\"targetpath\"))");
    }

    [TestMethod]
    public void AuditFlagsDynamicDependsAndPropertyFunctions()
    {
        using var project = CodegenUnitProject.FromScenario("dynamic-depends-property-function", "dynamic-depends-property-function.proj");
        using var document = project.Audit();
        var root = document.RootElement;

        Assert.AreEqual(1, root.GetProperty("counts").GetProperty("dynamicTargetDiagnostics").GetInt32());
        Assert.IsTrue(root.GetProperty("counts").GetProperty("propertyFunctionSites").GetInt32() >= 2);

        var dynamicTarget = root.GetProperty("diagnostics").GetProperty("dynamicTargets")[0];
        Assert.AreEqual("Build", dynamicTarget.GetProperty("target").GetString());
        Assert.AreEqual("dynamic-depends-on-targets", dynamicTarget.GetProperty("kind").GetString());

        AssertJsonArrayContains(root.GetProperty("diagnostics").GetProperty("propertyFunctionSites"), "expressionKind", "Condition");
        AssertJsonArrayContains(root.GetProperty("diagnostics").GetProperty("propertyFunctionSites"), "expressionKind", "Message.Text");
    }

    [TestMethod]
    public void AuditFlagsTargetAndTaskBatchingSites()
    {
        using var project = CodegenUnitProject.FromScenario("batching-diagnostics", "batching-diagnostics.proj");
        using var document = project.Audit();
        var root = document.RootElement;

        Assert.AreEqual(1, root.GetProperty("counts").GetProperty("targetBatchingSites").GetInt32());
        Assert.AreEqual(1, root.GetProperty("counts").GetProperty("taskBatchingSites").GetInt32());
        AssertJsonArrayContains(root.GetProperty("diagnostics").GetProperty("targetBatchingSites"), "target", "Build");
        AssertJsonArrayContains(root.GetProperty("diagnostics").GetProperty("taskBatchingSites"), "task", "Message");
    }

    [TestMethod]
    public void TaskBatchingGroupsItemsByQualifiedMetadata()
    {
        using var project = CodegenUnitProject.FromScenario("task-batching-qualified", "task-batching-qualified.csproj");
        var generated = project.Generate("Batching");
        generated.BuildHost();

        generated.RunHost().AssertSuccess("run generated qualified batching host");

        AssertFileLines(
            Path.Combine(project.DirectoryPath, "qualified.txt"),
            "1|Item1,Item4",
            "2|Item2,Item5",
            "3|Item3");
    }

    [TestMethod]
    public void TaskBatchingInfersUnqualifiedMetadataFromSingleItemList()
    {
        using var project = CodegenUnitProject.FromScenario("task-batching-unqualified", "task-batching-unqualified.csproj");
        var generated = project.Generate("Batching");
        generated.BuildHost();

        generated.RunHost().AssertSuccess("run generated unqualified batching host");

        AssertFileLines(
            Path.Combine(project.DirectoryPath, "unqualified.txt"),
            "1|Item1,Item4",
            "2|Item2");
    }

    [TestMethod]
    public void TaskBatchingConditionFiltersBatches()
    {
        using var project = CodegenUnitProject.FromScenario("task-batching-condition", "task-batching-condition.csproj");
        var generated = project.Generate("Batching");
        generated.BuildHost();

        generated.RunHost().AssertSuccess("run generated batching condition host");

        AssertFileLines(Path.Combine(project.DirectoryPath, "condition.txt"), "2|Item2,Item5");
    }

    [TestMethod]
    public void TaskBatchingGroupsDuplicateIdentities()
    {
        using var project = CodegenUnitProject.FromScenario("task-batching-identity", "task-batching-identity.csproj");
        var generated = project.Generate("Batching");
        generated.BuildHost();

        generated.RunHost().AssertSuccess("run generated identity batching host");

        AssertFileLines(
            Path.Combine(project.DirectoryPath, "identity.txt"),
            "One|One:red,One:blue",
            "Two|Two:green");
    }

    [TestMethod]
    public void TaskBatchingCombinesMultipleListsAndPassThroughLists()
    {
        using var project = CodegenUnitProject.FromScenario("task-batching-multiple-lists", "task-batching-multiple-lists.csproj");
        var generated = project.Generate("Batching");
        generated.BuildHost();

        generated.RunHost().AssertSuccess("run generated multiple-list batching host");

        AssertFileLines(
            Path.Combine(project.DirectoryPath, "multi.txt"),
            "1|A=A1,A3|B=B1|C=C1,C2",
            "2|A=A2|B=B2,B3|C=C1,C2");
    }

    [TestMethod]
    public void TaskBatchingSkipsEmptyBatchingSource()
    {
        using var project = CodegenUnitProject.FromScenario("task-batching-empty-source", "task-batching-empty-source.csproj");
        var generated = project.Generate("Batching");
        generated.BuildHost();

        generated.RunHost().AssertSuccess("run generated empty-source batching host");

        Assert.IsFalse(File.Exists(Path.Combine(project.DirectoryPath, "empty.txt")),
            "An empty batching source should not execute the task once with a synthetic empty item.");
    }

    [TestMethod]
    public void TaskBatchingAccumulatesOutputsInBatchOrder()
    {
        using var project = CodegenUnitProject.FromScenario("task-batching-outputs", "task-batching-outputs.csproj");
        var generated = project.Generate("Batching");
        generated.BuildHost();

        generated.RunHost().AssertSuccess("run generated batching outputs host");

        AssertFileLines(
            Path.Combine(project.DirectoryPath, "outputs.txt"),
            "1:Item1,Item3",
            "2:Item2");
    }

    [TestMethod]
    public void LocalTaskPathParametersNormalizeBackslashes()
    {
        using var project = CodegenUnitProject.FromScenario("path-separator-normalization", "path-separator-normalization.csproj");
        var generated = project.Generate("PathNormalization");

        StringAssert.Contains(generated.ProgramText, "PathUtil.NormalizePathLikeList");
        StringAssert.Contains(generated.ProgramText, "PathUtil.NormalizePathLike");

        generated.BuildHost();
        generated.RunHost().AssertSuccess("run generated path-normalization host");

        Assert.IsFalse(File.Exists(Path.Combine(project.DirectoryPath, "nested", "normalized.txt")));
        AssertFileLines(Path.Combine(project.DirectoryPath, "nested", "copied.txt"), "ok");
        Assert.IsFalse(File.Exists(Path.Combine(project.DirectoryPath, "nested", "mixed", "normalized.txt")));
        AssertFileLines(Path.Combine(project.DirectoryPath, "nested", "mixed", "copied.txt"), "mixed");
    }

    [TestMethod]
    public void MSBuildThisFilePropertiesAreInlinedFromDeclaringImport()
    {
        using var project = CodegenUnitProject.FromScenario("msbuild-this-file-context", "msbuild-this-file-context.csproj");
        var generated = project.Generate("ThisFileContext");

        var importDirectory = Path.Combine(project.DirectoryPath, "build") + Path.DirectorySeparatorChar;
        var importPath = Path.Combine(importDirectory, "Imported.ThisFile.targets");
        var outputPath = Path.Combine(importDirectory, "out.txt");

        StringAssert.Contains(generated.ProgramText, importPath);
        StringAssert.Contains(generated.ProgramText, outputPath);
        Assert.IsFalse(generated.ProgramText.Contains("P.MSBuildThisFileDirectory + \"out.txt\"", StringComparison.Ordinal),
            "Imported MSBuildThisFileDirectory must be emitted as a literal, not read from global P state.");

        generated.BuildHost();
        generated.RunHost().AssertSuccess("run generated MSBuildThisFile host");

        AssertFileLines(
            outputPath,
            $"Imported.ThisFile.targets|Imported.ThisFile|imported.thisfile|.targets|{importPath}|{importDirectory}|{importPath}|Imported.ThisFile");
    }

    [TestMethod]
    public void TaskBatchingSupportsMultipleMetadataFromSameItem()
    {
        using var project = CodegenUnitProject.FromScenario("task-batching-multiple-metadata", "task-batching-multiple-metadata.csproj");
        var generated = project.Generate("WriteBatches");

        Assert.IsFalse(generated.ProgramText.Contains("multi-dimensional task batching", StringComparison.Ordinal),
            "Tasks that reference multiple metadata names from one item type should still be emitted.");

        generated.BuildHost();
        generated.RunHost().AssertSuccess("run generated multiple-metadata batching host");

        AssertFileLines(
            Path.Combine(generated.OutputDirectory, "out", "batches.txt"),
            "alpha|one",
            "beta|two");
    }

    [TestMethod]
    public void GeneratorOmitsNoOpTasksAndMergesDirectItemCopyLoops()
    {
        using var project = CodegenUnitProject.FromScenario("generator-optimization", "generator-optimization.csproj");
        var generated = project.Generate("Optimize");

        Assert.IsFalse(generated.ProgramText.Contains("Tasks.AllowEmptyTelemetry(", StringComparison.Ordinal),
            "No-op telemetry tasks should not allocate parameters or emit calls.");
        Assert.IsFalse(generated.ProgramText.Contains("if (true)", StringComparison.Ordinal),
            "Literal true conditions should be folded away.");
        Assert.IsFalse(generated.ProgramText.Contains("P.SkippedProperty =", StringComparison.Ordinal),
            "Literal false property conditions should skip the assignment entirely.");
        Assert.AreEqual(
            1,
            CountOccurrences(generated.ProgramText, "foreach (var batchItem in I.Source)"),
            "Adjacent direct item-copy loops over the same source should be merged.");

        generated.BuildHost();
        generated.RunHost().AssertSuccess("run generated optimization host");

        AssertFileLines(Path.Combine(generated.OutputDirectory, "out", "first.txt"), "alpha");
        AssertFileLines(Path.Combine(generated.OutputDirectory, "out", "second.txt"), "beta");
        AssertFileLines(Path.Combine(generated.OutputDirectory, "out", "property.txt"), "folded", "");
    }

    [TestMethod]
    public void TargetBatchingIsRejectedDuringGeneration()
    {
        using var project = CodegenUnitProject.FromScenario("target-batching-unsupported", "target-batching-unsupported.csproj");
        var result = CommandRunner.Run(
            BsharpTestEnvironment.CodegenPath,
            ["--project", project.ProjectPath, "--out-dir", Path.Combine(project.DirectoryPath, "generated"), "--entry", "TargetBatching"],
            project.DirectoryPath,
            BsharpTestEnvironment.DotnetEnvironment,
            BsharpTestEnvironment.CommandTimeout);

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.StandardError + result.StandardOutput, "target batching is not supported");
        StringAssert.Contains(result.StandardError + result.StandardOutput, "TargetBatching");
        StringAssert.Contains(result.StandardError + result.StandardOutput, "Outputs");
    }

    [TestMethod]
    public void CallTargetForcesRuntimeDispatcherAndRootsTargets()
    {
        using var project = CodegenUnitProject.FromScenario("calltarget-dispatcher", "calltarget-dispatcher.proj");
        var generated = project.Generate();

        StringAssert.Contains(generated.Result.StandardOutput, "Targets emitted: 2 (all, due to CallTarget)");
        StringAssert.Contains(generated.ProgramText, "public static async ValueTask Run(string name)");
        StringAssert.Contains(generated.ProgramText, "if (string.Equals(name, \"Other\", StringComparison.OrdinalIgnoreCase)) { await T_002_Other(); return; }");
        StringAssert.Contains(generated.ProgramText, "using var taskLog = Log.Task(\"CallTarget\")");
    }

    [TestMethod]
    public void AuditReportsMSBuildTaskRecursionSites()
    {
        using var project = CodegenUnitProject.FromScenario("msbuild-task-unsupported", "msbuild-task-unsupported.proj");
        using var document = project.Audit();
        var root = document.RootElement;

        Assert.AreEqual(1, root.GetProperty("counts").GetProperty("msbuildTasks").GetInt32());
        var site = root.GetProperty("diagnostics").GetProperty("msbuildTasks")[0];
        Assert.AreEqual("Build", site.GetProperty("target").GetString());
        Assert.AreEqual("@(ProjectReference)", site.GetProperty("projects").GetString());
        Assert.AreEqual("Build", site.GetProperty("targets").GetString());
    }

    [TestMethod]
    public void MSBuildTaskCanReturnCrossProjectTargetOutputs()
    {
        using var project = CodegenUnitProject.FromScenario("msbuild-task-cross-project", "App.proj");
        var generated = project.Generate("CrossProject");
        generated.BuildHost();

        generated.RunHost().AssertSuccess("run generated cross-project MSBuild task host");

        AssertFileLines(
            Path.Combine(project.DirectoryPath, "references.txt"),
            Path.Combine(project.DirectoryPath, "bin", "Debug", "net11.0", "Lib.dll"));
    }

    [TestMethod]
    public void AuditClassifiesProjectReferenceShapes()
    {
        using var project = CodegenUnitProject.FromScenario("project-reference-shapes", "App.csproj");
        using var document = project.Audit();
        var root = document.RootElement;

        Assert.AreEqual(5, root.GetProperty("counts").GetProperty("projectAuthoredProjectReferences").GetInt32());
        Assert.AreEqual(4, root.GetProperty("counts").GetProperty("unsupportedProjectAuthoredProjectReferences").GetInt32());

        var references = root.GetProperty("diagnostics").GetProperty("projectReferences");
        var staticReference = FindProjectReference(references, "Lib\\StaticLib.csproj");
        Assert.AreEqual("static-supported-candidate", staticReference.GetProperty("status").GetString());
        Assert.IsTrue(staticReference.GetProperty("projectAuthored").GetBoolean());
        Assert.IsFalse(staticReference.GetProperty("dynamicInclude").GetBoolean());
        Assert.IsFalse(staticReference.GetProperty("wildcardInclude").GetBoolean());

        AssertProjectReferenceReason(references, "$(ReferenceProject)", "dynamic-include");
        AssertProjectReferenceReason(references, "Lib\\*.csproj", "wildcard-include");
        AssertProjectReferenceReason(references, "Lib\\NoReferenceOutput.csproj", "reference-output-assembly-false");
        AssertProjectReferenceReason(references, "Lib\\CustomTarget.csproj", "unsupported-metadata:Targets");
    }

    [TestMethod]
    public void AuditReportsDynamicImports()
    {
        using var project = CodegenUnitProject.FromScenario("dynamic-import", "dynamic-import.proj");
        using var document = project.Audit();
        var root = document.RootElement;

        Assert.AreEqual(1, root.GetProperty("counts").GetProperty("dynamicImports").GetInt32());
        var import = root.GetProperty("diagnostics").GetProperty("dynamicImports")[0];
        Assert.AreEqual("$(ImportName).targets", import.GetProperty("project").GetString());
        Assert.IsTrue(import.GetProperty("dynamicProject").GetBoolean());
    }

    [TestMethod]
    public void AuditRecordsGlobalProperties()
    {
        using var project = CodegenUnitProject.FromScenario("global-properties", "global-properties.proj");
        using var document = project.Audit("-p", "Configuration=Release");
        var root = document.RootElement;

        Assert.AreEqual("Release", root.GetProperty("globalProperties").GetProperty("Configuration").GetString());
        Assert.AreEqual("Release", root.GetProperty("shape").GetProperty("configuration").GetString());
    }

    [TestMethod]
    public void UnitScenarioManifestIsSourceTraceable()
    {
        var manifestPath = Path.Combine(BsharpTestEnvironment.RepoRoot, "fixtures", "msbuild-unit-scenarios", "unit-scenarios.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;

        Assert.AreEqual("dotnet-msbuild-unit-scenarios", root.GetProperty("name").GetString());
        Assert.AreEqual("911bea0b57d3613eb9c29f49ff9858d03884c397", root.GetProperty("upstreamCommit").GetString());

        foreach (var scenario in root.GetProperty("scenarios").EnumerateArray())
        {
            var projectFile = scenario.GetProperty("projectFile").GetString()!;
            var path = Path.Combine(BsharpTestEnvironment.RepoRoot, "fixtures", "msbuild-unit-scenarios", "cases", projectFile);
            Assert.IsTrue(File.Exists(path), $"Scenario project file is missing: {projectFile}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(scenario.GetProperty("upstreamPath").GetString()));
            Assert.IsFalse(string.IsNullOrWhiteSpace(scenario.GetProperty("upstreamTest").GetString()));
            var tier = scenario.GetProperty("tier").GetString();
            Assert.IsTrue(tier is "structural" or "runtime", $"Unexpected scenario tier: {tier}");
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static void AssertInOrder(string text, params string[] expected)
    {
        var index = -1;
        foreach (var value in expected)
        {
            var next = text.IndexOf(value, index + 1, StringComparison.Ordinal);
            Assert.IsTrue(next > index, $"Expected to find '{value}' after index {index}.");
            index = next;
        }
    }

    private static void AssertJsonArrayContains(JsonElement array, string propertyName, string expectedValue)
    {
        foreach (var element in array.EnumerateArray())
        {
            if (element.TryGetProperty(propertyName, out var property) &&
                property.GetString() == expectedValue)
            {
                return;
            }
        }

        Assert.Fail($"Expected JSON array to contain {propertyName}='{expectedValue}'.");
    }

    private static JsonElement FindProjectReference(JsonElement array, string include)
    {
        foreach (var element in array.EnumerateArray())
        {
            if (element.GetProperty("include").GetString() == include)
            {
                return element;
            }
        }

        Assert.Fail($"Expected ProjectReference include '{include}'.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static void AssertProjectReferenceReason(JsonElement array, string include, string expectedReason)
    {
        var reference = FindProjectReference(array, include);
        Assert.AreEqual("unsupported", reference.GetProperty("status").GetString());
        foreach (var reason in reference.GetProperty("unsupportedReasons").EnumerateArray())
        {
            if (reason.GetString() == expectedReason)
            {
                return;
            }
        }

        Assert.Fail($"Expected ProjectReference '{include}' to include unsupported reason '{expectedReason}'.");
    }

    private static void AssertFileLines(string path, params string[] expected)
    {
        Assert.IsTrue(File.Exists(path), $"Expected file to exist: {path}");
        CollectionAssert.AreEqual(expected, File.ReadAllLines(path));
    }
}
