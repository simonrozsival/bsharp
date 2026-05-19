using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Locator;
using System.Reflection;
using System.Text;
using System.Text.Json;

if (args.Length < 2) {
    Console.Error.WriteLine("usage: codegen --project <csproj> --out-dir <dir> [--entry Build] [-p Name=Value ...]");
    Console.Error.WriteLine("       codegen --audit --project <csproj> [-p Name=Value ...]");
    return 1;
}
if (args.Contains("--audit", StringComparer.OrdinalIgnoreCase) || (args.Length > 0 && args[0] == "audit")) {
    var auditProject = ArgValue(args, "--project") ?? args.FirstOrDefault(a => a.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
    if (auditProject == null) {
        Console.Error.WriteLine("usage: codegen --audit --project <csproj> [-p Name=Value ...]");
        return 1;
    }
    return Codegen.Audit(auditProject, CollectRepeated(args, "-p"));
}
if (args.Length < 4) {
    Console.Error.WriteLine("usage: codegen --project <csproj> --out-dir <dir> [--entry Build] [-p Name=Value ...]");
    return 1;
}
return Codegen.Run(
    projectPath:    ArgValue(args, "--project")!,
    outDir:         ArgValue(args, "--out-dir") ?? ArgValue(args, "--out")!,
    entryTarget:    ArgValue(args, "--entry") ?? "Build",
    globalProps:    CollectRepeated(args, "-p"));

static string? ArgValue(string[] args, string flag) {
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static Dictionary<string, string> CollectRepeated(string[] args, string flag) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length - 1; i++) {
        if (args[i] != flag) continue;
        var kv = args[i + 1];
        var eq = kv.IndexOf('=');
        if (eq > 0) result[kv.Substring(0, eq)] = kv.Substring(eq + 1);
    }
    return result;
}

static class Codegen {
    public static int Run(string projectPath, string outDir, string entryTarget, Dictionary<string, string> globalProps) {
        MSBuildLocator.RegisterDefaults();
        return RunInner(projectPath, outDir, entryTarget, globalProps);
    }

    public static int Audit(string projectPath, Dictionary<string, string> globalProps) {
        MSBuildLocator.RegisterDefaults();
        return AuditInner(projectPath, globalProps);
    }

    static int AuditInner(string projectPath, Dictionary<string, string> globalProps) {
        var pc = new ProjectCollection(globalProps);
        var fullPath = Path.GetFullPath(projectPath);
        var project = pc.LoadProject(fullPath);
        var instance = project.CreateProjectInstance();
        var taskRegistry = UsingTaskRegistry.Build(instance, project);
        var taskMetadata = TaskMetadataLoader.Load(taskRegistry);

        var targetDiagnostics = new List<object>();
        var taskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var callTargets = new List<object>();
        var msbuildTasks = new List<object>();
        var cscTasks = new List<object>();
        var taskBatchingSites = new List<object>();
        var targetBatchingSites = new List<object>();
        var propertyFunctionSites = new List<object>();

        foreach (var target in instance.Targets.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)) {
            var dynamicDepends = ContainsDynamicTargetList(target.DependsOnTargets);
            if (dynamicDepends) {
                targetDiagnostics.Add(new {
                    target = target.Name,
                    kind = "dynamic-depends-on-targets",
                    value = target.DependsOnTargets
                });
            }
            if (ContainsBatching(target.Inputs) || ContainsBatching(target.Outputs)) {
                targetBatchingSites.Add(new {
                    target = target.Name,
                    inputs = target.Inputs,
                    outputs = target.Outputs
                });
            }
            AddPropertyFunctionSite(propertyFunctionSites, target.Name, "Condition", target.Condition);
            AddPropertyFunctionSite(propertyFunctionSites, target.Name, "DependsOnTargets", target.DependsOnTargets);
            AddPropertyFunctionSite(propertyFunctionSites, target.Name, "Inputs", target.Inputs);
            AddPropertyFunctionSite(propertyFunctionSites, target.Name, "Outputs", target.Outputs);

            foreach (var child in target.Children) {
                switch (child) {
                    case ProjectTaskInstance task:
                        taskNames.Add(task.Name);
                        if (task.Name == "CallTarget") {
                            callTargets.Add(new {
                                target = target.Name,
                                targets = task.Parameters.TryGetValue("Targets", out var targets) ? targets : "",
                                dynamicTargets = task.Parameters.TryGetValue("Targets", out var callTargetValue) && ContainsDynamicTargetList(callTargetValue)
                            });
                        }
                        if (task.Name == "MSBuild") {
                            msbuildTasks.Add(new {
                                target = target.Name,
                                projects = task.Parameters.TryGetValue("Projects", out var projects) ? projects : "",
                                targets = task.Parameters.TryGetValue("Targets", out var targets) ? targets : "",
                                properties = task.Parameters.TryGetValue("Properties", out var properties) ? properties : ""
                            });
                        }
                        if (task.Name == "Csc") {
                            cscTasks.Add(new {
                                target = target.Name,
                                sources = task.Parameters.TryGetValue("Sources", out var sources) ? sources : "",
                                references = task.Parameters.TryGetValue("References", out var references) ? references : "",
                                analyzers = task.Parameters.TryGetValue("Analyzers", out var analyzers) ? analyzers : "",
                                additionalFiles = task.Parameters.TryGetValue("AdditionalFiles", out var additionalFiles) ? additionalFiles : "",
                                analyzerConfigFiles = task.Parameters.TryGetValue("AnalyzerConfigFiles", out var analyzerConfigFiles) ? analyzerConfigFiles : "",
                                generatedFilesOutputPath = task.Parameters.TryGetValue("GeneratedFilesOutputPath", out var generatedFilesOutputPath) ? generatedFilesOutputPath : "",
                                skipAnalyzers = task.Parameters.TryGetValue("SkipAnalyzers", out var skipAnalyzers) ? skipAnalyzers : "",
                                useSharedCompilation = task.Parameters.TryGetValue("UseSharedCompilation", out var useSharedCompilation) ? useSharedCompilation : ""
                            });
                        }
                        AddPropertyFunctionSite(propertyFunctionSites, target.Name, task.Name + ".Condition", task.Condition);
                        foreach (var parameter in task.Parameters) {
                            if (ContainsBatching(parameter.Value)) {
                                taskBatchingSites.Add(new {
                                    target = target.Name,
                                    task = task.Name,
                                    parameter = parameter.Key,
                                    value = parameter.Value
                                });
                            }
                            AddPropertyFunctionSite(propertyFunctionSites, target.Name, task.Name + "." + parameter.Key, parameter.Value);
                        }
                        break;
                    case ProjectPropertyGroupTaskInstance propertyGroup:
                        AddPropertyFunctionSite(propertyFunctionSites, target.Name, "PropertyGroup.Condition", propertyGroup.Condition);
                        foreach (var property in propertyGroup.Properties) {
                            AddPropertyFunctionSite(propertyFunctionSites, target.Name, "Property:" + property.Name, property.Value);
                            if (ContainsBatching(property.Value)) {
                                taskBatchingSites.Add(new {
                                    target = target.Name,
                                    task = "PropertyGroup",
                                    parameter = property.Name,
                                    value = property.Value
                                });
                            }
                        }
                        break;
                    case ProjectItemGroupTaskInstance itemGroup:
                        AddPropertyFunctionSite(propertyFunctionSites, target.Name, "ItemGroup.Condition", itemGroup.Condition);
                        foreach (var item in itemGroup.Items) {
                            AddPropertyFunctionSite(propertyFunctionSites, target.Name, "Item:" + item.ItemType + ".Include", item.Include);
                            AddPropertyFunctionSite(propertyFunctionSites, target.Name, "Item:" + item.ItemType + ".Remove", item.Remove);
                            if (ContainsBatching(item.Include) || ContainsBatching(item.Remove)) {
                                taskBatchingSites.Add(new {
                                    target = target.Name,
                                    task = "ItemGroup",
                                    parameter = item.ItemType,
                                    include = item.Include,
                                    remove = item.Remove
                                });
                            }
                        }
                        break;
                }
            }
        }

        var importingElements = project.Imports
            .Select(i => i.ImportingElement)
            .Where(e => e != null)
            .Select(e => new {
                project = e!.Project,
                condition = e.Condition,
                dynamicProject = ContainsPropertyOrItemReference(e.Project)
            })
            .Where(e => e.dynamicProject)
            .Take(200)
            .ToArray();

        var audit = new {
            schemaVersion = 1,
            generatedAtUtc = DateTime.UtcNow,
            project = fullPath,
            globalProperties = globalProps.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(kv => kv.Key, kv => kv.Value),
            shape = new {
                targetFramework = instance.GetPropertyValue("TargetFramework"),
                targetFrameworks = instance.GetPropertyValue("TargetFrameworks"),
                runtimeIdentifier = instance.GetPropertyValue("RuntimeIdentifier"),
                runtimeIdentifiers = instance.GetPropertyValue("RuntimeIdentifiers"),
                configuration = instance.GetPropertyValue("Configuration"),
                platform = instance.GetPropertyValue("Platform"),
                targetPlatformIdentifier = instance.GetPropertyValue("TargetPlatformIdentifier"),
                targetPlatformVersion = instance.GetPropertyValue("TargetPlatformVersion"),
                useMaui = instance.GetPropertyValue("UseMaui"),
                isOuterBuild = string.IsNullOrEmpty(instance.GetPropertyValue("TargetFramework")) &&
                               !string.IsNullOrEmpty(instance.GetPropertyValue("TargetFrameworks"))
            },
            counts = new {
                targets = instance.Targets.Count,
                items = instance.Items.Count,
                properties = instance.Properties.Count,
                usedTaskNames = taskNames.Count,
                usingTasks = taskRegistry.EntryCount,
                usingTaskAssemblies = taskRegistry.AssemblyCount,
                taskMetadataTypes = taskMetadata.TaskCount,
                taskMetadataLoadErrors = taskMetadata.LoadErrors.Count,
                callTargets = callTargets.Count,
                msbuildTasks = msbuildTasks.Count,
                cscTasks = cscTasks.Count,
                dynamicImports = importingElements.Length,
                dynamicTargetDiagnostics = targetDiagnostics.Count,
                targetBatchingSites = targetBatchingSites.Count,
                taskBatchingSites = taskBatchingSites.Count,
                propertyFunctionSites = propertyFunctionSites.Count,
                inlineUsingTasks = taskRegistry.Entries.Count(e => e.IsInline),
                unresolvedUsingTasks = taskRegistry.Entries.Count(e => e.Status != "resolved")
            },
            diagnostics = new {
                outerBuild = string.IsNullOrEmpty(instance.GetPropertyValue("TargetFramework")) &&
                             !string.IsNullOrEmpty(instance.GetPropertyValue("TargetFrameworks"))
                    ? "Project evaluated as an outer build. MAUI support requires an outer dispatcher that creates per-TargetFramework inner generated hosts."
                    : null,
                dynamicImports = importingElements,
                dynamicTargets = targetDiagnostics.Take(200).ToArray(),
                callTargets = callTargets.Take(200).ToArray(),
                msbuildTasks = msbuildTasks.Take(200).ToArray(),
                cscTasks = cscTasks.Take(200).ToArray(),
                targetBatchingSites = targetBatchingSites.Take(200).ToArray(),
                taskBatchingSites = taskBatchingSites.Take(200).ToArray(),
                propertyFunctionSites = propertyFunctionSites.Take(200).ToArray(),
                inlineUsingTasks = taskRegistry.Entries.Where(e => e.IsInline).Select(e => new {
                    e.TaskName,
                    e.ExpandedTaskName,
                    e.Condition,
                    e.Notes
                }).ToArray(),
                unresolvedUsingTasks = taskRegistry.Entries.Where(e => e.Status != "resolved").Select(e => new {
                    e.TaskName,
                    e.ExpandedTaskName,
                    e.DeclaredAssembly,
                    e.Status,
                    e.Notes,
                    e.IsUsed
                }).ToArray(),
                taskMetadataLoadErrors = taskMetadata.LoadErrors.Take(100).ToArray()
            }
        };

        Console.WriteLine(JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    static int RunInner(string projectPath, string outDir, string entryTarget, Dictionary<string, string> globalProps) {
        var pc = new ProjectCollection(globalProps);
        var project = pc.LoadProject(Path.GetFullPath(projectPath));
        var instance = project.CreateProjectInstance();

        // If any target invokes CallTarget, we need to emit method bodies for EVERY target
        // in the project (CallTarget can name anything at runtime) and we need the
        // Targets.Run(string) dispatcher to exist. Detect this up front and pass it through.
        bool hasCallTarget = HasCallTarget(instance);
        var sequence = ComputeTargetSequence(instance, entryTarget, includeAllTargets: hasCallTarget);

        var propNames = CollectPropertyNames(project, instance, sequence);
        var itemTypes = CollectItemTypeNames(project, instance, sequence);
        Emitter.SetRegistries(propNames, itemTypes);
        Emitter.SetCallTargetFlag(hasCallTarget);

        Directory.CreateDirectory(outDir);

        // Phase 1 of v1.5 — scan UsingTasks and resolve each TaskName → (Assembly, Type).
        // Phase 1b: load each task DLL via MetadataLoadContext and extract typed property
        // metadata. Both currently produce printable reports; Phase 3 will consume the
        // typed metadata when emitting real task instantiations.
        var taskRegistry = UsingTaskRegistry.Build(instance, project);
        var taskMetadata = TaskMetadataLoader.Load(taskRegistry);
        var reportPath = Path.Combine(outDir, "tasks.report.txt");
        File.WriteAllText(reportPath, taskRegistry.RenderReport() + "\n" + RenderTaskMetadataReport(taskMetadata));
        Emitter.SetTaskMetadata(taskMetadata);

        var sb = new StringBuilder();
        Emitter.Emit(sb, project, instance, sequence, entryTarget);
        var programPath = Path.Combine(outDir, "Program.cs");
        File.WriteAllText(programPath, sb.ToString());

        // Shared task invocation model used by the generated host and persistent task server.
        var taskModelPath = Path.Combine(outDir, "TaskModel.cs");
        File.WriteAllText(taskModelPath, TaskModelSource());

        var csprojPath = Path.Combine(outDir, "BsharpGenerated.csproj");
        File.WriteAllText(csprojPath, ProjectTemplate(taskRegistry, taskMetadata));

        var taskServerDir = Path.Combine(outDir, "task-server");
        if (Directory.Exists(taskServerDir)) Directory.Delete(taskServerDir, recursive: true);
        Directory.CreateDirectory(taskServerDir);
        EmitTaskServer(taskServerDir, taskRegistry, taskMetadata, outDir);
        var legacyTasksDir = Path.Combine(outDir, "tasks");
        if (Directory.Exists(legacyTasksDir)) Directory.Delete(legacyTasksDir, recursive: true);

        Console.WriteLine($"Wrote {outDir}/");
        Console.WriteLine($"  Entry target: {entryTarget}");
        Console.WriteLine($"  Targets emitted: {sequence.Count}{(hasCallTarget ? " (all, due to CallTarget)" : "")}");
        Console.WriteLine($"  Typed properties: {propNames.Count}");
        Console.WriteLine($"  Typed item types: {itemTypes.Count}");
        Console.WriteLine($"  UsingTask entries: {taskRegistry.EntryCount} across {taskRegistry.AssemblyCount} assemblies → {reportPath}");
        Console.WriteLine($"  Task metadata loaded: {taskMetadata.TaskCount} types{(taskMetadata.LoadErrors.Count > 0 ? $", {taskMetadata.LoadErrors.Count} load errors" : "")}");
        if (globalProps.Count > 0)
            Console.WriteLine($"  Global properties: {string.Join(", ", globalProps.Select(kv => $"{kv.Key}={kv.Value}"))}");
        return 0;
    }

    static bool ContainsDynamicTargetList(string? value) =>
        ContainsPropertyOrItemReference(value);

    static bool ContainsPropertyOrItemReference(string? value) =>
        !string.IsNullOrEmpty(value) &&
        (value.Contains("$(", StringComparison.Ordinal) ||
         value.Contains("@(", StringComparison.Ordinal) ||
         value.Contains("%(", StringComparison.Ordinal));

    static bool ContainsBatching(string? value) =>
        !string.IsNullOrEmpty(value) && value.Contains("%(", StringComparison.Ordinal);

    static void AddPropertyFunctionSite(List<object> sites, string target, string expressionKind, string? value) {
        if (string.IsNullOrEmpty(value) || !value.Contains("$([", StringComparison.Ordinal))
            return;
        sites.Add(new {
            target,
            expressionKind,
            value
        });
    }

    // The shared task invocation model emitted as TaskModel.cs. Compiled into the
    // generated host and the persistent task server.
    //
    // JSON fallback shape (request, sent on stdin; response, returned on stdout):
    //   { "Properties": { "Foo": "string", "Bar": true, "Baz": [{"Identity": "x", "Metadata": {"k":"v"}}, ...], ... } }
    //   { "Success": true, "Outputs": { "OutItem": [...], "OutProp": "value", ... }, "Error": null }
    // JsonElement-valued maps let the codegen choose typed deserialization per-output
    // without us inventing a tagged-union sum type.
    static string TaskModelSource() => """
// <auto-generated/>
// Shared task invocation model for bsharp real SDK task execution. The generated host
// and persistent task server exchange these objects over a length-prefixed JSON stream.
#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bsharp.Generated.TaskModel;

public sealed class TaskInvocation {
    public string TaskName { get; set; } = "";
    // Each property maps the task-parameter name to a JSON value of an arbitrary shape:
    //   string  → JsonValueKind.String
    //   bool    → JsonValueKind.True/False
    //   int     → JsonValueKind.Number
    //   ITaskItem  → JSON object { "Identity": "...", "Metadata": { ... } }
    //   ITaskItem[]→ JSON array of the above
    //   string[]   → JSON array of strings
    // The task metadata knows the destination CLR type for each property and
    // deserializes accordingly via the helpers below.
    public Dictionary<string, JsonElement> Properties { get; set; } = new();
}

public sealed class TaskResult {
    public bool Success { get; set; }
    public Dictionary<string, JsonElement> Outputs { get; set; } = new();
    public string? Error { get; set; }
}

// Portable shape for ITaskItem. Roundtrips Identity + Metadata cleanly when serialized.
public sealed class ItemSpec {
    public string Identity { get; set; } = "";
    public Dictionary<string, string>? Metadata { get; set; }
}

// Source-generated context. Listing every concrete top-level type we serialize.
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TaskInvocation))]
[JsonSerializable(typeof(TaskResult))]
[JsonSerializable(typeof(ItemSpec))]
[JsonSerializable(typeof(ItemSpec[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class TaskModelJson : JsonSerializerContext { }

// Convenience builders/readers. Generated codegen uses these to set/get the right shape
// for each task parameter or output without sprinkling JsonElement parsing everywhere.
public static class TaskModelExt {
    public static void SetString(this TaskInvocation r, string name, string value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.String);
    public static void SetBool(this TaskInvocation r, string name, bool value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.Boolean);
    public static void SetInt(this TaskInvocation r, string name, int value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.Int32);
    public static void SetLong(this TaskInvocation r, string name, long value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.Int64);
    public static void SetDouble(this TaskInvocation r, string name, double value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.Double);
    public static void SetStrings(this TaskInvocation r, string name, string[] value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.StringArray);
    public static void SetItem(this TaskInvocation r, string name, ItemSpec? value) {
        if (value == null) return;
        r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.ItemSpec);
    }
    public static void SetItems(this TaskInvocation r, string name, ItemSpec[] value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.ItemSpecArray);

    // Task invocation uses these to extract typed values from the request map.
    public static string GetString(this TaskInvocation r, string name)
        => r.Properties.TryGetValue(name, out var e) ? (e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : e.ToString()) : "";
    public static bool GetBool(this TaskInvocation r, string name)
        => r.Properties.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.True;
    public static int GetInt(this TaskInvocation r, string name)
        => r.Properties.TryGetValue(name, out var e) && e.TryGetInt32(out var v) ? v : 0;
    public static long GetLong(this TaskInvocation r, string name)
        => r.Properties.TryGetValue(name, out var e) && e.TryGetInt64(out var v) ? v : 0L;
    public static double GetDouble(this TaskInvocation r, string name)
        => r.Properties.TryGetValue(name, out var e) && e.TryGetDouble(out var v) ? v : 0d;
    public static string[] GetStrings(this TaskInvocation r, string name)
        => r.Properties.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.Array
           ? (JsonSerializer.Deserialize(e, TaskModelJson.Default.StringArray) ?? Array.Empty<string>())
           : Array.Empty<string>();
    public static ItemSpec? GetItem(this TaskInvocation r, string name)
        => r.Properties.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.Object
           ? JsonSerializer.Deserialize(e, TaskModelJson.Default.ItemSpec)
           : null;
    public static ItemSpec[] GetItems(this TaskInvocation r, string name)
        => r.Properties.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.Array
           ? (JsonSerializer.Deserialize(e, TaskModelJson.Default.ItemSpecArray) ?? Array.Empty<ItemSpec>())
           : Array.Empty<ItemSpec>();

    // Symmetric helpers for outputs on the response side.
    public static void SetString(this TaskResult r, string name, string? value) {
        if (value == null) return;
        r.Outputs[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.String);
    }
    public static void SetItems(this TaskResult r, string name, ItemSpec[] value)
        => r.Outputs[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.ItemSpecArray);
    public static string[] GetStrings(this TaskResult r, string name)
        => r.Outputs.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.Array
           ? (JsonSerializer.Deserialize(e, TaskModelJson.Default.StringArray) ?? Array.Empty<string>())
           : Array.Empty<string>();
    public static ItemSpec[] GetItems(this TaskResult r, string name)
        => r.Outputs.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.Array
           ? (JsonSerializer.Deserialize(e, TaskModelJson.Default.ItemSpecArray) ?? Array.Empty<ItemSpec>())
           : Array.Empty<ItemSpec>();
    public static string GetString(this TaskResult r, string name)
        => r.Outputs.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : "";
}
""";

    // Render the loaded task-type metadata section for the tasks.report.txt file.
    // Lists every task type we successfully loaded, with its public settable properties
    // (the parameter set Phase 3 will read) and any load errors encountered.
    static string RenderTaskMetadataReport(TaskMetadataLoader.MetadataIndex meta) {
        var sb = new StringBuilder();
        sb.AppendLine("# Task type metadata (MetadataLoadContext)");
        sb.AppendLine($"# Loaded types: {meta.TaskCount}  Load errors: {meta.LoadErrors.Count}");
        sb.AppendLine();
        foreach (var err in meta.LoadErrors) sb.AppendLine($"!! {err}");
        if (meta.LoadErrors.Count > 0) sb.AppendLine();
        foreach (var kv in meta.ByTaskName.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) {
            var t = kv.Value;
            sb.Append("## ").Append(t.FullTypeName);
            sb.Append("  (from ").Append(Path.GetFileName(t.AssemblyPath)).AppendLine(")");
            if (t.Properties.Count == 0) {
                sb.AppendLine("    (no settable properties)");
            } else {
                foreach (var p in t.Properties) {
                    var flags = "";
                    if (p.IsRequired) flags += "[Required] ";
                    if (p.IsOutput)   flags += "[Output] ";
                    if (!p.CanWrite)  flags += "[ReadOnly] ";
                    sb.Append("    ").Append(p.PropertyTypeShort.PadRight(18)).Append(' ')
                      .Append(p.Name).Append(' ').AppendLine(flags.TrimEnd());
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // Returns true if any target in the project invokes <CallTarget ... />.
    // CallTarget makes target invocation dynamic — we need the runtime dispatcher and
    // method bodies for all targets, not just those reachable from the entry via static deps.
    static bool HasCallTarget(ProjectInstance instance) {
        foreach (var target in instance.Targets.Values)
            foreach (var child in target.Children)
                if (child is ProjectTaskInstance task && task.Name == "CallTarget")
                    return true;
        return false;
    }

    // Find the MSBuild reference assembly directory by walking up from any resolved task DLL.
    // Required for the generated csproj to have valid <Reference Include=".../Microsoft.Build.Framework.dll"/>
    // that the C# compiler can bind against at compile time.
    static string? FindSdkRootWithFrameworkDll(UsingTaskRegistry.Registry registry) {
        foreach (var entry in registry.Entries) {
            if (entry.ResolvedAssemblyPath == null) continue;
            var d = Path.GetDirectoryName(entry.ResolvedAssemblyPath);
            while (d != null) {
                if (File.Exists(Path.Combine(d, "Microsoft.Build.Framework.dll")))
                    return d;
                d = Path.GetDirectoryName(d);
            }
        }
        return null;
    }

    static string ProjectTemplate(UsingTaskRegistry.Registry registry, TaskMetadataLoader.MetadataIndex meta) {
        // The generated host stays NativeAOT-friendly. Real SDK task assemblies are not
        // referenced here; they run inside the persistent CoreCLR task server.
        var sb = new StringBuilder();
        sb.AppendLine("""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>full</TrimMode>
    <StripSymbols>true</StripSymbols>
    <OptimizationPreference>Speed</OptimizationPreference>
    <StackTraceSupport>false</StackTraceSupport>
    <UseSystemResourceKeys>true</UseSystemResourceKeys>
    <DebuggerSupport>false</DebuggerSupport>
    <EventSourceSupport>false</EventSourceSupport>
    <NoWarn>$(NoWarn);IL3000;CS8012</NoWarn>
    <RootNamespace>Bsharp.Generated</RootNamespace>
    <AssemblyName>BsharpGenerated</AssemblyName>
    <!-- Auxiliary task server lives below this directory; exclude it from this csproj's Compile glob. -->
    <DefaultItemExcludes>$(DefaultItemExcludes);task-server/**/*</DefaultItemExcludes>
  </PropertyGroup>
  <ItemGroup>
    <!-- Hashing utility used by Tasks.Hash. Pin to the SDK shipping version. -->
    <PackageReference Include="System.IO.Hashing" Version="11.0.0-preview.4.26208.110" />
  </ItemGroup>
""");
        sb.AppendLine("""
</Project>
""");
        // Task metadata is consumed by the generated task registry.
        _ = meta;
        return sb.ToString();
    }

    static void EmitTaskServer(string serverDir, UsingTaskRegistry.Registry registry, TaskMetadataLoader.MetadataIndex meta, string sharedOutDir) {
        var sdkRoot = FindSdkRootWithFrameworkDll(registry);
        File.WriteAllText(Path.Combine(serverDir, "Program.cs"), TaskServerProgramCs(meta));
        File.WriteAllText(Path.Combine(serverDir, "BsharpTaskServer.csproj"), TaskServerCsproj(sdkRoot, sharedOutDir, serverDir));
    }

    static string TaskServerCsproj(string? sdkRoot, string sharedOutDir, string serverDir) {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <OutputType>Exe</OutputType>");
        sb.AppendLine("    <TargetFramework>net11.0</TargetFramework>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine("    <InvariantGlobalization>true</InvariantGlobalization>");
        sb.AppendLine("    <AssemblyName>BsharpTaskServer</AssemblyName>");
        sb.AppendLine("    <NoWarn>$(NoWarn);CS8012;CS8602;IL2026</NoWarn>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine($"    <Compile Include=\"{Path.GetRelativePath(serverDir, Path.Combine(sharedOutDir, "TaskModel.cs"))}\" />");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("  <ItemGroup>");
        if (sdkRoot != null) {
            foreach (var asm in new[] { "Microsoft.Build.Framework.dll", "Microsoft.Build.Utilities.Core.dll", "Microsoft.Build.dll" }) {
                var path = Path.Combine(sdkRoot, asm);
                if (File.Exists(path))
                    sb.AppendLine($"    <Reference Include=\"{Path.GetFileNameWithoutExtension(asm)}\"><HintPath>{path}</HintPath><Private>true</Private></Reference>");
            }
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    static string TaskServerProgramCs(TaskMetadataLoader.MetadataIndex meta) {
        var sb = new StringBuilder();
        sb.AppendLine("""
// <auto-generated/>
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Bsharp.Generated.TaskModel;

Console.SetOut(TextWriter.Null);
Console.SetError(TextWriter.Null);
TaskRegistry.Register();
TaskServer.Run(Console.OpenStandardInput(), Console.OpenStandardOutput());

sealed record TaskDescriptor(string ShortName, string FullTypeName, string AssemblyPath, string[] OutputNames);

static class TaskRegistry {
    public static readonly Dictionary<string, TaskDescriptor> Tasks = new(StringComparer.OrdinalIgnoreCase);
    public static void Add(string shortName, string fullTypeName, string assemblyPath, string[] outputNames) =>
        Tasks[shortName] = new TaskDescriptor(shortName, fullTypeName, assemblyPath, outputNames);
    public static void Register() {
""");
        foreach (var t in meta.ByTaskName.Values.OrderBy(t => t.FullTypeName, StringComparer.Ordinal)) {
            var shortName = t.FullTypeName.Contains('.')
                ? t.FullTypeName.Substring(t.FullTypeName.LastIndexOf('.') + 1)
                : t.FullTypeName;
            var outputs = t.OutputProperties.Count == 0
                ? "Array.Empty<string>()"
                : $"new[] {{ {string.Join(", ", t.OutputProperties.Select(p => Emitter.CSharpLiteral(p.Name)))} }}";
            sb.AppendLine($"        Add({Emitter.CSharpLiteral(shortName)}, {Emitter.CSharpLiteral(t.FullTypeName)}, {Emitter.CSharpLiteral(t.AssemblyPath)}, {outputs});");
        }
        sb.AppendLine("""
    }
}

static class TaskServer {
    static readonly Dictionary<string, TaskDirectoryLoadContext> ContextsByDir = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Type> TypesByShortName = new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> PropertiesByType = new();
    static readonly Microsoft.Build.Framework.TaskEnvironment TaskEnvironment =
        Microsoft.Build.Framework.TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(Directory.GetCurrentDirectory());

    public static void Run(Stream input, Stream output) {
        while (true) {
            var payload = ReadFrame(input);
            if (payload == null) return;
            var req = JsonSerializer.Deserialize(payload, TaskModelJson.Default.TaskInvocation) ?? new TaskInvocation();
            var resp = Execute(req);
            WriteFrame(output, JsonSerializer.SerializeToUtf8Bytes(resp, TaskModelJson.Default.TaskResult));
        }
    }

    static TaskResult Execute(TaskInvocation req) {
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());
        try {
            if (!TaskRegistry.Tasks.TryGetValue(req.TaskName, out var desc))
                return new TaskResult { Success = false, Error = $"task '{req.TaskName}' is not registered" };
            var type = GetTaskType(desc);
            var task = Activator.CreateInstance(type) ?? throw new InvalidOperationException($"Could not create task '{desc.FullTypeName}'");
            SetBuildEngine(type, task);
            SetTaskEnvironment(type, task);
            foreach (var kv in req.Properties) SetValue(type, task, kv.Key, kv.Value);
            var guard = CaptureTimestampGuard(req, desc);
            BsharpBuildEngine.CapturedErrors.Clear();
            var execute = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMethodException(desc.FullTypeName, "Execute");
            var success = execute.Invoke(task, Array.Empty<object?>()) as bool? ?? false;
            RestoreTimestampIfContentUnchanged(guard);
            var resp = new TaskResult { Success = success };
            foreach (var outputName in desc.OutputNames) CaptureOutput(resp, type, task, outputName);
            if (!success) resp.Error = BsharpBuildEngine.CapturedErrors.Count > 0 ? string.Join("\n", BsharpBuildEngine.CapturedErrors) : $"task '{desc.ShortName}' returned false";
            return resp;
        } catch (TargetInvocationException ex) when (ex.InnerException != null) {
            return new TaskResult { Success = false, Error = ex.InnerException.GetType().Name + ": " + ex.InnerException.Message + "\n" + ex.InnerException.StackTrace };
        } catch (Exception ex) {
            return new TaskResult { Success = false, Error = ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace };
        } finally {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }
    }

    static byte[]? ReadFrame(Stream input) {
        Span<byte> lenBytes = stackalloc byte[4];
        var read = input.Read(lenBytes);
        if (read == 0) return null;
        while (read < 4) { var n = input.Read(lenBytes[read..]); if (n == 0) throw new EndOfStreamException(); read += n; }
        var len = BitConverter.ToInt32(lenBytes);
        if (len < 0 || len > 64 * 1024 * 1024) throw new InvalidDataException($"invalid frame length {len}");
        var payload = new byte[len];
        var offset = 0;
        while (offset < len) { var n = input.Read(payload, offset, len - offset); if (n == 0) throw new EndOfStreamException(); offset += n; }
        return payload;
    }
    static void WriteFrame(Stream output, byte[] payload) {
        Span<byte> lenBytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(lenBytes, payload.Length);
        output.Write(lenBytes);
        output.Write(payload);
        output.Flush();
    }
    static Type GetTaskType(TaskDescriptor desc) {
        if (TypesByShortName.TryGetValue(desc.ShortName, out var cached)) return cached;
        var dir = Path.GetDirectoryName(desc.AssemblyPath)!;
        if (!ContextsByDir.TryGetValue(dir, out var alc)) { alc = new TaskDirectoryLoadContext(desc.AssemblyPath); ContextsByDir[dir] = alc; }
        var asm = alc.Assemblies.FirstOrDefault(a => string.Equals(a.Location, desc.AssemblyPath, StringComparison.OrdinalIgnoreCase)) ?? alc.LoadFromAssemblyPath(desc.AssemblyPath);
        var type = asm.GetType(desc.FullTypeName, throwOnError: true)!;
        TypesByShortName[desc.ShortName] = type;
        return type;
    }
    static void SetBuildEngine(Type type, object task) {
        var prop = type.GetProperty("BuildEngine", BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.CanWrite) prop.SetValue(task, new BsharpBuildEngine());
    }
    static void SetTaskEnvironment(Type type, object task) {
        var prop = type.GetProperty("TaskEnvironment", BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(Microsoft.Build.Framework.TaskEnvironment))
            prop.SetValue(task, TaskEnvironment);
    }
    static void SetValue(Type type, object task, string name, JsonElement value) {
        var prop = GetProperty(type, name);
        if (prop == null || !prop.CanWrite) return;
        var targetType = prop.PropertyType;
        object? converted = null;
        if (targetType == typeof(string)) converted = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
        else if (targetType == typeof(bool)) converted = value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b) && b);
        else if (targetType == typeof(int) || targetType == typeof(int?)) converted = value.TryGetInt32(out var i) ? i : 0;
        else if (targetType == typeof(long) || targetType == typeof(long?)) converted = value.TryGetInt64(out var l) ? l : 0L;
        else if (targetType == typeof(double) || targetType == typeof(double?)) converted = value.TryGetDouble(out var d) ? d : 0d;
        else if (targetType == typeof(string[])) converted = JsonSerializer.Deserialize(value, TaskModelJson.Default.StringArray) ?? Array.Empty<string>();
        else if (typeof(Microsoft.Build.Framework.ITaskItem).IsAssignableFrom(targetType))
            converted = JsonSerializer.Deserialize(value, TaskModelJson.Default.ItemSpec) is { } spec ? new BsharpTaskItem(spec) : null;
        else if (targetType.IsArray && typeof(Microsoft.Build.Framework.ITaskItem).IsAssignableFrom(targetType.GetElementType())) {
            var specs = JsonSerializer.Deserialize(value, TaskModelJson.Default.ItemSpecArray) ?? Array.Empty<ItemSpec>();
            var arr = Array.CreateInstance(targetType.GetElementType()!, specs.Length);
            for (int idx = 0; idx < specs.Length; idx++) arr.SetValue(new BsharpTaskItem(specs[idx]), idx);
            converted = arr;
        }
        if (converted != null) prop.SetValue(task, converted);
    }
    static PropertyInfo? GetProperty(Type type, string name) {
        var props = PropertiesByType.GetOrAdd(type, static t => {
            var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) map[prop.Name] = prop;
            return map;
        });
        return props.TryGetValue(name, out var prop) ? prop : null;
    }
    static void CaptureOutput(TaskResult resp, Type type, object task, string name) {
        var value = GetProperty(type, name)?.GetValue(task);
        switch (value) {
            case null: break;
            case string s: resp.SetString(name, s); break;
            case bool b: resp.SetString(name, b ? "true" : "false"); break;
            case string[] strings: resp.Outputs[name] = JsonSerializer.SerializeToElement(strings, TaskModelJson.Default.StringArray); break;
            case Microsoft.Build.Framework.ITaskItem item: resp.SetItems(name, new[] { ToSpec(item) }); break;
            case Microsoft.Build.Framework.ITaskItem[] items: resp.SetItems(name, items.Select(ToSpec).ToArray()); break;
            default: resp.SetString(name, value.ToString() ?? ""); break;
        }
    }
    static Bsharp.Generated.TaskModel.ItemSpec ToSpec(Microsoft.Build.Framework.ITaskItem item) {
        var spec = new Bsharp.Generated.TaskModel.ItemSpec { Identity = item.ItemSpec };
        foreach (var k in item.MetadataNames) { var key = k?.ToString() ?? ""; if (key.Length > 0 && !MetadataHelpers.IsWellKnown(key)) (spec.Metadata ??= new())[key] = item.GetMetadata(key) ?? ""; }
        return spec;
    }
    static (string Path, byte[] Content, DateTime TimestampUtc)? CaptureTimestampGuard(TaskInvocation req, TaskDescriptor desc) {
        var path = desc.ShortName switch { "WriteCodeFragment" => req.GetString("OutputFile"), "GenerateMSBuildEditorConfig" => req.GetString("File"), _ => "" };
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        return (path, File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
    }
    static void RestoreTimestampIfContentUnchanged((string Path, byte[] Content, DateTime TimestampUtc)? guard) {
        if (guard is not { } g || !File.Exists(g.Path)) return;
        var current = File.ReadAllBytes(g.Path);
        if (current.AsSpan().SequenceEqual(g.Content)) File.SetLastWriteTimeUtc(g.Path, g.TimestampUtc);
    }
}

sealed class TaskDirectoryLoadContext : AssemblyLoadContext {
    readonly AssemblyDependencyResolver _resolver;
    readonly string _dir;
    readonly string? _sdkRoot;
    public TaskDirectoryLoadContext(string primaryAssemblyPath) : base("bsharp-task:" + Path.GetDirectoryName(primaryAssemblyPath), isCollectible: false) {
        _resolver = new AssemblyDependencyResolver(primaryAssemblyPath);
        _dir = Path.GetDirectoryName(primaryAssemblyPath)!;
        _sdkRoot = FindSdkRoot(_dir);
    }
    protected override Assembly? Load(AssemblyName assemblyName) {
        if (IsSharedBuildAssembly(assemblyName.Name)) {
            foreach (var asm in AssemblyLoadContext.Default.Assemblies)
                if (AssemblyName.ReferenceMatchesDefinition(assemblyName, asm.GetName())) return asm;
            if (_sdkRoot != null) {
                var sharedPath = Path.Combine(_sdkRoot, assemblyName.Name + ".dll");
                if (File.Exists(sharedPath)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(sharedPath);
            }
            return Assembly.Load(assemblyName);
        }
        var sibling = Path.Combine(_dir, assemblyName.Name + ".dll");
        if (File.Exists(sibling)) return LoadFromAssemblyPath(sibling);
        if (_sdkRoot != null) { var sdkSibling = Path.Combine(_sdkRoot, assemblyName.Name + ".dll"); if (File.Exists(sdkSibling)) return LoadFromAssemblyPath(sdkSibling); }
        var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        return resolved != null ? LoadFromAssemblyPath(resolved) : null;
    }
    static bool IsSharedBuildAssembly(string? name) => name is "Microsoft.Build.Framework" or "Microsoft.Build.Utilities.Core" or "Microsoft.Build";
    static string? FindSdkRoot(string start) {
        for (var d = start; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "Microsoft.Build.Framework.dll"))) return d;
        return null;
    }
}
""");
        sb.AppendLine(TaskAdapterSource());
        return sb.ToString();
    }

    // Common BsharpBuildEngine + BsharpTaskItem code used by the persistent task server.
    public static string TaskAdapterSource() => """

// MSBuild's well-known item metadata names — these are always derivable from Identity
// and shouldn't be transported in task item metadata. We exclude them when
// serializing task output items so we don't pollute the downstream ItemSpec.Metadata
// (which would later trip MSBuild's MSB3095 "FullPath is reserved" check).
internal static class MetadataHelpers {
    static readonly HashSet<string> WellKnown = new(StringComparer.OrdinalIgnoreCase) {
        "FullPath", "RootDir", "Filename", "Extension", "RelativeDir", "Directory",
        "RecursiveDir", "Identity", "ModifiedTime", "CreatedTime", "AccessedTime",
        "DefiningProjectFullPath", "DefiningProjectDirectory",
        "DefiningProjectName", "DefiningProjectExtension",
    };
    public static bool IsWellKnown(string name) => WellKnown.Contains(name);
}

sealed class BsharpTaskItem : Microsoft.Build.Framework.ITaskItem, Microsoft.Build.Framework.ITaskItem2 {
    readonly Bsharp.Generated.TaskModel.ItemSpec _spec;
    public BsharpTaskItem(Bsharp.Generated.TaskModel.ItemSpec spec) {
        _spec = spec;
        // Ensure metadata lookups are case-insensitive. JSON deserialization gives us a
        // case-sensitive dictionary by default; MSBuild metadata is case-insensitive.
        if (spec.Metadata != null && !ReferenceEquals(spec.Metadata.Comparer, StringComparer.OrdinalIgnoreCase)) {
            spec.Metadata = new Dictionary<string, string>(spec.Metadata, StringComparer.OrdinalIgnoreCase);
        }
    }
    public Bsharp.Generated.TaskModel.ItemSpec Inner => _spec;
    public string ItemSpec {
        get => _spec.Identity;
        set => _spec.Identity = value;
    }
    public System.Collections.ICollection MetadataNames {
        // Return ONLY user metadata. The 7 well-known names (FullPath, Filename, etc.)
        // are derivable from Identity and Microsoft.Build.Utilities.TaskItem doesn't
        // surface them via MetadataNames either — including them here causes us to
        // serialize useless empty values on the response and pollutes the ItemSpec.Metadata
        // map on the receiving side.
        get => _spec.Metadata?.Keys ?? (System.Collections.ICollection)Array.Empty<string>();
    }
    public int MetadataCount => _spec.Metadata?.Count ?? 0;
    public string GetMetadata(string name) => name?.ToLowerInvariant() switch {
        null => "",
        "identity"    => _spec.Identity,
        "fullpath"    => Path.GetFullPath(_spec.Identity),
        "filename"    => Path.GetFileNameWithoutExtension(_spec.Identity),
        "extension"   => Path.GetExtension(_spec.Identity),
        "directory"   => Path.GetDirectoryName(_spec.Identity) ?? "",
        "rootdir"     => Path.GetPathRoot(_spec.Identity) ?? "",
        "relativedir" => Path.GetDirectoryName(_spec.Identity) is string d && d.Length > 0 ? d + Path.DirectorySeparatorChar : "",
        // Case-insensitive lookup (the ctor normalized the dict above) — handles both
        // mixed-case keys from JSON and lowercase keys set via SetMetadata.
        _ => _spec.Metadata != null && _spec.Metadata.TryGetValue(name!, out var v) ? v : "",
    };
    public void SetMetadata(string name, string value) {
        (_spec.Metadata ??= new(StringComparer.OrdinalIgnoreCase))[name] = value ?? "";
    }
    public void RemoveMetadata(string name) {
        _spec.Metadata?.Remove(name);
    }
    public void CopyMetadataTo(Microsoft.Build.Framework.ITaskItem destinationItem) {
        if (_spec.Metadata == null) return;
        foreach (var kv in _spec.Metadata) destinationItem.SetMetadata(kv.Key, kv.Value);
    }
    public System.Collections.IDictionary CloneCustomMetadata() {
        var d = new System.Collections.Hashtable();
        if (_spec.Metadata != null) foreach (var kv in _spec.Metadata) d[kv.Key] = kv.Value;
        return d;
    }
    public string EvaluatedIncludeEscaped { get => _spec.Identity; set => _spec.Identity = value; }
    public string GetMetadataValueEscaped(string name) => GetMetadata(name);
    public void SetMetadataValueLiteral(string name, string value) => SetMetadata(name, value);
    public System.Collections.IDictionary CloneCustomMetadataEscaped() => CloneCustomMetadata();
    public override string ToString() => _spec.Identity;
}

sealed class BsharpBuildEngine
    : Microsoft.Build.Framework.IBuildEngine,
      Microsoft.Build.Framework.IBuildEngine2,
      Microsoft.Build.Framework.IBuildEngine3,
      Microsoft.Build.Framework.IBuildEngine4,
      Microsoft.Build.Framework.IBuildEngine5,
      Microsoft.Build.Framework.IBuildEngine6,
      Microsoft.Build.Framework.IBuildEngine7,
      Microsoft.Build.Framework.IBuildEngine8,
      Microsoft.Build.Framework.IBuildEngine9,
      Microsoft.Build.Framework.IBuildEngine10
{
    public string ProjectFileOfTaskNode => "";
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public bool ContinueOnError => false;
    public bool IsRunningMultipleNodes => false;

    // Capture errors so they can be embedded in the JSON response. Do not write directly
    // to stderr here: ignored target failures should stay quiet, and the task server's
    // stderr is redirected as a protocol safety boundary.
    public static readonly List<string> CapturedErrors = new();

    public void LogMessageEvent(Microsoft.Build.Framework.BuildMessageEventArgs e) { }
    public void LogWarningEvent(Microsoft.Build.Framework.BuildWarningEventArgs e) { }
    public void LogErrorEvent(Microsoft.Build.Framework.BuildErrorEventArgs e) {
        var line = string.IsNullOrEmpty(e.Code) ? $"error: {e.Message}" : $"error {e.Code}: {e.Message}";
        CapturedErrors.Add(line);
    }
    public void LogCustomEvent(Microsoft.Build.Framework.CustomBuildEventArgs e) { }

    public bool BuildProjectFile(string projectFileName, string[]? targetNames, System.Collections.IDictionary? globalProperties, System.Collections.IDictionary? targetOutputs) {
        if (string.IsNullOrEmpty(projectFileName)) return true;
        throw new NotSupportedException("<MSBuild> recursive task is not supported in the task server");
    }
    public bool BuildProjectFile(string projectFileName, string[]? targetNames, System.Collections.IDictionary? globalProperties, System.Collections.IDictionary? targetOutputs, string? toolsVersion) => BuildProjectFile(projectFileName, targetNames, globalProperties, targetOutputs);
    public bool BuildProjectFilesInParallel(string[] projectFileNames, string[] targetNames, System.Collections.IDictionary[] globalProperties, System.Collections.IDictionary[] targetOutputsPerProject, string[] toolsVersion, bool useResultsCache, bool unloadProjectsOnCompletion) { if (projectFileNames == null || projectFileNames.Length == 0) return true; throw new NotSupportedException("BuildProjectFilesInParallel"); }
    public Microsoft.Build.Framework.BuildEngineResult BuildProjectFilesInParallel(string[] projectFileNames, string[] targetNames, System.Collections.IDictionary[] globalProperties, IList<string>[] removeGlobalProperties, string[] toolsVersion, bool returnTargetOutputs) { if (projectFileNames == null || projectFileNames.Length == 0) return new Microsoft.Build.Framework.BuildEngineResult(true, new List<IDictionary<string, Microsoft.Build.Framework.ITaskItem[]>>()); throw new NotSupportedException("BuildProjectFilesInParallel"); }

    public void Yield() { }
    public void Reacquire() { }

    readonly Dictionary<(string, Microsoft.Build.Framework.RegisteredTaskObjectLifetime), object> _taskObjs = new();
    public void RegisterTaskObject(object key, object obj, Microsoft.Build.Framework.RegisteredTaskObjectLifetime lifetime, bool allowEarlyCollection) => _taskObjs[(key?.ToString() ?? "", lifetime)] = obj;
    public object? GetRegisteredTaskObject(object key, Microsoft.Build.Framework.RegisteredTaskObjectLifetime lifetime) => _taskObjs.TryGetValue((key?.ToString() ?? "", lifetime), out var v) ? v : null;
    public object? UnregisterTaskObject(object key, Microsoft.Build.Framework.RegisteredTaskObjectLifetime lifetime) { var k = (key?.ToString() ?? "", lifetime); if (_taskObjs.TryGetValue(k, out var v)) { _taskObjs.Remove(k); return v; } return null; }

    public void LogTelemetry(string eventName, IDictionary<string, string> properties) { }
    public IReadOnlyDictionary<string, string> GetGlobalProperties() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool AllowFailureWithoutError { get; set; }
    public bool ShouldTreatWarningAsError(string warningCode) => false;
    public int RequestCores(int requestedCores) => requestedCores;
    public void ReleaseCores(int coresToRelease) { }
    public Microsoft.Build.Framework.EngineServices EngineServices => BsharpEngineServices.Instance;
}

// EngineServices is abstract; several SDK tasks dereference engine.EngineServices
// directly without null-checking (TaskLoggingHelper.LogsMessagesOfImportance et al).
// We provide a permissive implementation so basic Log* calls don't NRE.
sealed class BsharpEngineServices : Microsoft.Build.Framework.EngineServices {
    public static readonly BsharpEngineServices Instance = new();
    public override bool LogsMessagesOfImportance(Microsoft.Build.Framework.MessageImportance importance) => true;
    public override bool IsTaskInputLoggingEnabled => false;
}
""";

    // Topological sort over targets reachable from `entry` via DependsOnTargets,
    // BeforeTargets, AfterTargets. Mirrors MSBuild's own target scheduling order.
    // When includeAllTargets is true (CallTarget present), starts with every target
    // in the project so the runtime dispatcher can name any of them.
    static List<string> ComputeTargetSequence(ProjectInstance instance, string entry, bool includeAllTargets = false) {
        // Resolve $(X) refs inside DependsOnTargets/Before/AfterTargets using the
        // evaluated project. These are normally stable at the moment the build starts.
        string Expand(string? s) {
            if (string.IsNullOrEmpty(s)) return "";
            try { return instance.ExpandString(s); } catch { return s; }
        }
        string[] Deps(string? s) {
            var expanded = Expand(s);
            if (string.IsNullOrEmpty(expanded)) return Array.Empty<string>();
            return expanded.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        }

        // Build reverse-index for BeforeTargets / AfterTargets: target name -> list of targets
        // that have it in their BeforeTargets / AfterTargets attribute.
        var beforeIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var afterIndex  = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in instance.Targets.Values) {
            foreach (var b in Deps(t.BeforeTargets)) {
                if (!beforeIndex.TryGetValue(b, out var list)) { list = new(); beforeIndex[b] = list; }
                list.Add(t.Name);
            }
            foreach (var a in Deps(t.AfterTargets)) {
                if (!afterIndex.TryGetValue(a, out var list)) { list = new(); afterIndex[a] = list; }
                list.Add(t.Name);
            }
        }

        // Reachability closure: include entry + everything reachable via DependsOn / Before / After.
        // If CallTarget is in use, seed with EVERY defined target so we emit method bodies for all.
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var work = new Queue<string>();
        if (includeAllTargets) {
            foreach (var name in instance.Targets.Keys) { reachable.Add(name); work.Enqueue(name); }
        } else {
            if (instance.Targets.ContainsKey(entry)) {
                reachable.Add(entry); work.Enqueue(entry);
            }
            if (!string.Equals(entry, "Restore", StringComparison.OrdinalIgnoreCase) &&
                instance.Targets.ContainsKey("Restore")) {
                reachable.Add("Restore"); work.Enqueue("Restore");
            }
        }
        while (work.Count > 0) {
            var name = work.Dequeue();
            if (!instance.Targets.TryGetValue(name, out var t)) continue;
            foreach (var d in Deps(t.DependsOnTargets)) if (reachable.Add(d)) work.Enqueue(d);
            if (beforeIndex.TryGetValue(name, out var b)) foreach (var x in b) if (reachable.Add(x)) work.Enqueue(x);
            if (afterIndex.TryGetValue(name, out var a)) foreach (var x in a) if (reachable.Add(x)) work.Enqueue(x);
        }

        // Build a predecessor (must-run-before) graph over reachable targets.
        var preds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in reachable) preds[name] = new(StringComparer.OrdinalIgnoreCase);
        foreach (var name in reachable) {
            if (!instance.Targets.TryGetValue(name, out var t)) continue;
            foreach (var d in Deps(t.DependsOnTargets))
                if (reachable.Contains(d)) preds[name].Add(d);
            // BeforeTargets: this target runs before the named targets → named target gets `name` as predecessor.
            foreach (var b in Deps(t.BeforeTargets))
                if (reachable.Contains(b)) preds[b].Add(name);
            // AfterTargets: this target runs after the named targets → this target gets `b` as predecessor.
            foreach (var a in Deps(t.AfterTargets))
                if (reachable.Contains(a)) preds[name].Add(a);
        }

        // Topological order (DFS).
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        void Visit(string n) {
            if (!reachable.Contains(n)) return;
            if (visited.Contains(n)) return;
            if (onStack.Contains(n)) return; // cycle — ignore the back-edge
            onStack.Add(n);
            if (preds.TryGetValue(n, out var ps)) foreach (var p in ps) Visit(p);
            onStack.Remove(n);
            visited.Add(n);
            result.Add(n);
        }
        Visit(entry);
        // Any unconnected reachable targets — append in stable order.
        foreach (var n in reachable.OrderBy(x => x, StringComparer.Ordinal)) Visit(n);
        return result;
    }

    static Dictionary<string, string> CollectPropertyNames(
        Microsoft.Build.Evaluation.Project project, ProjectInstance instance, List<string> sequence)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string name) {
            if (string.IsNullOrEmpty(name)) return;
            if (!IsValidCSharpIdent(name)) return;
            map.TryAdd(name.ToLowerInvariant(), name);
        }
        // Reserved properties (always present)
        Add("MSBuildProjectFullPath"); Add("MSBuildProjectFile"); Add("MSBuildProjectName");
        Add("MSBuildProjectExtension"); Add("MSBuildProjectDirectory"); Add("ProjectDir");
        Add("MSBuildThisFile"); Add("MSBuildThisFileFullPath"); Add("MSBuildThisFileDirectory");
        Add("TargetFramework"); Add("TargetFrameworks"); Add("TargetPath"); Add("RuntimeFrameworkVersion");
        foreach (var p in project.AllEvaluatedProperties) Add(p.Name);
        foreach (var name in sequence) {
            if (!instance.Targets.TryGetValue(name, out var t)) continue;
            ScanExpr(t.Condition, Add); ScanExpr(t.Inputs, Add); ScanExpr(t.Outputs, Add);
            foreach (var c in t.Children) ScanChildProps(c, Add);
        }
        return map;
    }

    static Dictionary<string, string> CollectItemTypeNames(
        Microsoft.Build.Evaluation.Project project, ProjectInstance instance, List<string> sequence)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string name) {
            if (string.IsNullOrEmpty(name)) return;
            if (!IsValidCSharpIdent(name)) return;
            map.TryAdd(name.ToLowerInvariant(), name);
        }
        foreach (var item in project.AllEvaluatedItems) Add(item.ItemType);
        foreach (var name in sequence) {
            if (!instance.Targets.TryGetValue(name, out var t)) continue;
            ScanItemRefs(t.Condition, Add); ScanItemRefs(t.Inputs, Add); ScanItemRefs(t.Outputs, Add);
            foreach (var c in t.Children) ScanChildItems(c, Add);
        }
        return map;
    }

    static void ScanChildProps(ProjectTargetInstanceChild c, Action<string> add) {
        switch (c) {
            case ProjectPropertyGroupTaskInstance pg:
                ScanExpr(pg.Condition, add);
                foreach (var p in pg.Properties) { add(p.Name); ScanExpr(p.Condition, add); ScanExpr(p.Value, add); }
                break;
            case ProjectItemGroupTaskInstance ig:
                ScanExpr(ig.Condition, add);
                foreach (var i in ig.Items) {
                    ScanExpr(i.Condition, add); ScanExpr(i.Include, add); ScanExpr(i.Remove, add);
                    foreach (var m in i.Metadata) { ScanExpr(m.Condition, add); ScanExpr(m.Value, add); }
                }
                break;
            case ProjectTaskInstance task:
                ScanExpr(task.Condition, add);
                foreach (var kv in task.Parameters) ScanExpr(kv.Value, add);
                foreach (var o in task.Outputs) if (o is ProjectTaskOutputPropertyInstance op) add(op.PropertyName);
                break;
        }
    }

    static void ScanChildItems(ProjectTargetInstanceChild c, Action<string> add) {
        switch (c) {
            case ProjectPropertyGroupTaskInstance pg:
                ScanItemRefs(pg.Condition, add);
                foreach (var p in pg.Properties) { ScanItemRefs(p.Condition, add); ScanItemRefs(p.Value, add); }
                break;
            case ProjectItemGroupTaskInstance ig:
                ScanItemRefs(ig.Condition, add);
                foreach (var i in ig.Items) {
                    add(i.ItemType);
                    ScanItemRefs(i.Condition, add); ScanItemRefs(i.Include, add); ScanItemRefs(i.Remove, add);
                    foreach (var m in i.Metadata) { ScanItemRefs(m.Condition, add); ScanItemRefs(m.Value, add); }
                }
                break;
            case ProjectTaskInstance task:
                ScanItemRefs(task.Condition, add);
                foreach (var kv in task.Parameters) ScanItemRefs(kv.Value, add);
                foreach (var o in task.Outputs) if (o is ProjectTaskOutputItemInstance oi) add(oi.ItemType);
                break;
        }
    }

    static void ScanExpr(string? expr, Action<string> add) {
        if (string.IsNullOrEmpty(expr)) return;
        int i = 0;
        while (i < expr.Length - 1) {
            if (expr[i] == '$' && expr[i + 1] == '(') {
                int j = expr.IndexOf(')', i + 2);
                if (j < 0) break;
                var inner = expr.Substring(i + 2, j - i - 2);
                if (!inner.StartsWith("[") && !inner.Contains("$") && !inner.Contains("(") && !inner.Contains(".")) add(inner);
                i = j + 1;
            } else i++;
        }
    }

    static void ScanItemRefs(string? expr, Action<string> add) {
        if (string.IsNullOrEmpty(expr)) return;
        int i = 0;
        while (i < expr.Length - 1) {
            if (expr[i] == '@' && expr[i + 1] == '(') {
                int j = expr.IndexOf(')', i + 2);
                if (j < 0) break;
                var inner = expr.Substring(i + 2, j - i - 2);
                int sep = inner.IndexOfAny(new[] { '-', ',' });
                add(sep >= 0 ? inner.Substring(0, sep).Trim() : inner.Trim());
                i = j + 1;
            } else if (expr[i] == '%' && expr[i + 1] == '(') {
                // Qualified metadata batch: %(ItemType.MetadataName) references ItemType.
                int j = expr.IndexOf(')', i + 2);
                if (j < 0) break;
                var inner = expr.Substring(i + 2, j - i - 2);
                int dot = inner.IndexOf('.');
                if (dot > 0) add(inner.Substring(0, dot).Trim());
                i = j + 1;
            } else i++;
        }
    }

    static bool IsValidCSharpIdent(string s) {
        if (string.IsNullOrEmpty(s)) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        for (int i = 1; i < s.Length; i++) {
            if (!char.IsLetterOrDigit(s[i]) && s[i] != '_') return false;
        }
        return !CSharpReserved.Contains(s);
    }

    static readonly HashSet<string> CSharpReserved = new(StringComparer.Ordinal) {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const",
        "continue","decimal","default","delegate","do","double","else","enum","event","explicit",
        "extern","false","finally","fixed","float","for","foreach","goto","if","implicit","in","int",
        "interface","internal","is","lock","long","namespace","new","null","object","operator","out",
        "override","params","private","protected","public","readonly","ref","return","sbyte","sealed",
        "short","sizeof","stackalloc","static","string","struct","switch","this","throw","true","try",
        "typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while",
    };
}

// v1.5 Phase 1: scan UsingTask declarations and resolve each TaskName → (Assembly, Type).
// Walks ProjectInstance.UsingTasks (which is already post-property-substitution and
// post-Condition-filtered by the MSBuild evaluator) so we don't have to re-evaluate
// $(MicrosoftNETBuildTasksAssembly), the various TFM conditions, etc. ourselves.
//
// What we produce: per-task entry { TaskName, ResolvedAssemblyPath, ChosenTypeName,
// DeclaredCondition, IsInline, ResolutionStatus, Notes }. Plus a printable report that
// later phases (and humans) can consult.
//
// We deliberately do NOT load the DLLs here — that's Phase 1b
// (v15-codegen-task-metadata) — but we DO record the resolved on-disk path so the next
// step can MetadataLoadContext.LoadFromAssemblyPath() without re-resolving.
static class UsingTaskRegistry {
    public sealed class Entry {
        public required string TaskName;                 // raw value from XML (may contain $(X) refs)
        public required string ExpandedTaskName;         // post-property-substitution; this is the CLR type name
        public required string DeclaredAssembly;         // pre-expansion form for diagnostics
        public required string? ResolvedAssemblyPath;    // null if we couldn't resolve
        public required string? Condition;
        public required string ConditionStatus;          // passes | fails | unknown (unknown means we couldn't evaluate)
        public required bool IsInline;                    // <UsingTask> with <Task> body or TaskFactory= attribute
        public required bool IsUsed;                      // appears as a <Task> in some target body
        public required string Status;                    // resolved | unresolved | inline-unsupported
        public string? Notes;
    }

    public sealed class Registry {
        public required List<Entry> Entries;
        public required Dictionary<string, Entry> ByTaskName; // case-insensitive last-wins (matches MSBuild)
        public required HashSet<string> AmbiguousShortNames;
        public int EntryCount => Entries.Count;
        public int AssemblyCount => Entries.Where(e => e.ResolvedAssemblyPath != null)
                                           .Select(e => e.ResolvedAssemblyPath!)
                                           .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        public string RenderReport() {
            var sb = new StringBuilder();
            sb.AppendLine("# bsharp UsingTask binding report");
            sb.AppendLine($"# Generated at: {DateTime.UtcNow:u}");
            sb.AppendLine($"# Entries: {EntryCount}  (used in this project: {Entries.Count(e => e.IsUsed)})");
            sb.AppendLine($"# Unique resolved assemblies: {AssemblyCount}");
            if (AmbiguousShortNames.Count > 0) {
                sb.AppendLine($"# Ambiguous short names (full name required to invoke): {string.Join(", ", AmbiguousShortNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}");
            }
            sb.AppendLine();

            var groups = Entries
                .GroupBy(e => new {
                    Used = e.IsUsed,
                    Resolved = e.ResolvedAssemblyPath != null,
                    e.Status,
                    Cond = e.ConditionStatus,
                })
                .OrderByDescending(g => g.Key.Used)
                .ThenByDescending(g => g.Key.Resolved)
                .ThenBy(g => g.Key.Cond, StringComparer.Ordinal);

            foreach (var g in groups) {
                var label = g.Key.Used ? "USED" : "DECLARED-BUT-UNUSED";
                sb.AppendLine($"## {label}  status={g.Key.Status}  cond={g.Key.Cond}  count={g.Count()}");
                foreach (var e in g.OrderBy(x => x.ExpandedTaskName, StringComparer.OrdinalIgnoreCase)) {
                    sb.Append($"  {e.ExpandedTaskName,-60}");
                    if (e.ExpandedTaskName != e.TaskName) sb.Append($"  (raw: {e.TaskName})");
                    if (e.ResolvedAssemblyPath != null) sb.Append("  → ").Append(e.ResolvedAssemblyPath);
                    else sb.Append("  → !! UNRESOLVED: declared as `").Append(e.DeclaredAssembly).Append('`');
                    if (e.Condition != null) sb.Append($"  [Condition: {e.Condition}]");
                    if (e.Notes != null) sb.Append($"  -- ").Append(e.Notes);
                    sb.AppendLine();
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }

    enum ConditionResult { Passes, Fails, Unknown }

    public static Registry Build(ProjectInstance instance, Project project) {
        var entries = new List<Entry>();
        var byName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var shortAliasUsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shortAliasAmbiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Find which task names are actually invoked in any target body. We only emit
        // typed bindings for the used set — declared-but-unused are reported but skipped.
        var usedTaskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in instance.Targets.Values)
            foreach (var child in t.Children)
                if (child is ProjectTaskInstance task) usedTaskNames.Add(task.Name);

        // Collect from all imported ProjectRootElements + the root XML itself. MSBuild's
        // public API doesn't surface a flat "evaluated UsingTasks" collection on
        // ProjectInstance, so we walk the construction model and post-filter by Condition
        // ourselves.
        var seenSources = new HashSet<ProjectRootElement>();
        var sources = new List<ProjectRootElement> { project.Xml };
        foreach (var imp in project.Imports)
            if (imp.ImportedProject != null) sources.Add(imp.ImportedProject);

        // Step 1: register MSBuild's built-in intrinsic tasks. MSBuild knows these by name
        // without any <UsingTask> declaration; we synthesize fake entries here so the
        // codegen typed-task path picks them up automatically. Microsoft.Build.Tasks.Core.dll
        // lives in the SDK root next to Microsoft.Build.Framework.dll.
        var intrinsicsAsmPath = FindIntrinsicsAsmPath(instance);
        if (intrinsicsAsmPath != null) {
            foreach (var (taskShortName, fullTypeName) in IntrinsicTaskMap) {
                if (!usedTaskNames.Contains(taskShortName)) continue;
                var entry = new Entry {
                    TaskName = taskShortName,
                    ExpandedTaskName = fullTypeName,
                    DeclaredAssembly = "(intrinsic)",
                    ResolvedAssemblyPath = intrinsicsAsmPath,
                    Condition = null,
                    ConditionStatus = "passes",
                    IsInline = false,
                    IsUsed = true,
                    Status = "resolved",
                    Notes = "MSBuild intrinsic (no <UsingTask> in the SDK; synthesized by bsharp)",
                };
                entries.Add(entry);
                byName[fullTypeName] = entry;
                byName[taskShortName] = entry; // intrinsics are always invoked by short name; this is safe
                shortAliasUsed[taskShortName] = fullTypeName;
            }
        }

        // Step 2: walk the SDK XML for explicit <UsingTask> entries.
        foreach (var pre in sources) {
            if (!seenSources.Add(pre)) continue;
            foreach (var ut in pre.UsingTasks) {
                var condStatus = EvaluateCondition(instance, ut.Condition);
                if (condStatus == ConditionResult.Fails) continue;

                var declaredAsm = !string.IsNullOrEmpty(ut.AssemblyFile) ? ut.AssemblyFile :
                                 !string.IsNullOrEmpty(ut.AssemblyName) ? ut.AssemblyName : "";
                string? resolvedPath = null;
                string status = "resolved";
                string? notes = null;
                bool isInline = ut.TaskBody != null || !string.IsNullOrEmpty(ut.TaskFactory);

                if (isInline) {
                    status = "inline-unsupported";
                    notes = ut.TaskBody != null
                        ? "TaskBody (inline) is not supported in v1; declared but not bindable"
                        : $"TaskFactory='{ut.TaskFactory}' (factory binding) is not supported in v1";
                } else if (!string.IsNullOrEmpty(ut.AssemblyFile)) {
                    var expanded = TryExpand(instance, ut.AssemblyFile);
                    if (File.Exists(expanded)) resolvedPath = Path.GetFullPath(expanded);
                    else if (TryResolveOnSearchPath(instance, expanded) is string fromPath) {
                        resolvedPath = fromPath;
                    } else {
                        status = "unresolved";
                        notes = $"AssemblyFile path does not exist after expansion: '{expanded}'";
                    }
                } else if (!string.IsNullOrEmpty(ut.AssemblyName)) {
                    status = "unresolved";
                    notes = $"AssemblyName='{ut.AssemblyName}' (no AssemblyFile) — unsupported in v1";
                } else {
                    status = "unresolved";
                    notes = "no AssemblyFile or AssemblyName specified";
                }

                // Expand $(...) in TaskName too. Several SDKs declare TaskName="$(MSBuildThisFileName).Tasks.X"
                // and the expanded form is the actual CLR type name to bind against.
                var rawTaskName = ut.TaskName;
                var expandedTaskName = TryExpand(instance, rawTaskName);
                var shortName = ShortName(expandedTaskName);
                bool isUsed = (shortName != null && usedTaskNames.Contains(shortName))
                              || usedTaskNames.Contains(expandedTaskName);

                var entry = new Entry {
                    TaskName = rawTaskName,
                    ExpandedTaskName = expandedTaskName,
                    DeclaredAssembly = declaredAsm,
                    ResolvedAssemblyPath = resolvedPath,
                    Condition = string.IsNullOrEmpty(ut.Condition) ? null : ut.Condition,
                    ConditionStatus = condStatus == ConditionResult.Passes ? "passes" : "unknown",
                    IsInline = isInline,
                    IsUsed = isUsed,
                    Status = status,
                    Notes = notes,
                };

                entries.Add(entry);

                // Last-wins for full task name. Short-name aliasing is dangerous when distinct
                // full names share a short name (e.g. Microsoft.SourceLink.GitHub.GetSourceLinkUrl
                // vs Microsoft.SourceLink.GitLab.GetSourceLinkUrl), so we only set a short-name
                // alias when it would be UNAMBIGUOUS — if another full name already claims it,
                // we drop the short-name alias entirely and leave callers to use full names.
                byName[expandedTaskName] = entry;
                if (shortName != null && shortName != expandedTaskName) {
                    if (shortAliasUsed.TryGetValue(shortName, out var prev) && prev != expandedTaskName) {
                        // Collision — keep both full-name bindings but drop the short alias.
                        byName.Remove(shortName);
                        shortAliasAmbiguous.Add(shortName);
                    } else if (!shortAliasAmbiguous.Contains(shortName)) {
                        byName[shortName] = entry;
                        shortAliasUsed[shortName] = expandedTaskName;
                    }
                }
            }
        }

        return new Registry {
            Entries = entries,
            ByTaskName = byName,
            AmbiguousShortNames = shortAliasAmbiguous,
        };
    }

    // Evaluate an MSBuild Condition string against the current project state.
    // Returns Passes/Fails for known patterns; Unknown for anything we can't reason about.
    //   - empty/null → Passes
    //   - literal "true"/"false" → Passes/Fails
    //   - 'A' == 'B' / 'A' != 'B' after ExpandString → literal compare
    //   - Exists('path') — Passes iff File.Exists or Directory.Exists after ExpandString
    //   - !Exists('path') — inverse
    // Anything else (boolean And/Or, property functions like HasTrailingSlash, complex
    // mixed expressions) returns Unknown — caller decides whether to include.
    static ConditionResult EvaluateCondition(ProjectInstance instance, string? condition) {
        if (string.IsNullOrEmpty(condition)) return ConditionResult.Passes;
        string expanded;
        try { expanded = instance.ExpandString(condition).Trim(); }
        catch { return ConditionResult.Unknown; }
        if (expanded.Length == 0) return ConditionResult.Passes;
        if (string.Equals(expanded, "true", StringComparison.OrdinalIgnoreCase)) return ConditionResult.Passes;
        if (string.Equals(expanded, "false", StringComparison.OrdinalIgnoreCase)) return ConditionResult.Fails;

        // Exists('path') and !Exists('path')
        if (TryParseExists(expanded, out var path, out var negated)) {
            var exists = File.Exists(path) || Directory.Exists(path);
            var result = negated ? !exists : exists;
            return result ? ConditionResult.Passes : ConditionResult.Fails;
        }

        if (expanded.Contains("==")) {
            var parts = expanded.Split("==", 2);
            return string.Equals(StripQuotes(parts[0]), StripQuotes(parts[1]), StringComparison.OrdinalIgnoreCase)
                ? ConditionResult.Passes : ConditionResult.Fails;
        }
        if (expanded.Contains("!=")) {
            var parts = expanded.Split("!=", 2);
            return !string.Equals(StripQuotes(parts[0]), StripQuotes(parts[1]), StringComparison.OrdinalIgnoreCase)
                ? ConditionResult.Passes : ConditionResult.Fails;
        }
        return ConditionResult.Unknown;
    }

    static bool TryParseExists(string expr, out string path, out bool negated) {
        path = ""; negated = false;
        var s = expr.Trim();
        if (s.StartsWith("!", StringComparison.Ordinal)) { negated = true; s = s.Substring(1).Trim(); }
        if (!s.StartsWith("Exists(", StringComparison.OrdinalIgnoreCase)) return false;
        if (!s.EndsWith(")", StringComparison.Ordinal)) return false;
        var inner = s.Substring("Exists(".Length, s.Length - "Exists(".Length - 1).Trim();
        path = StripQuotes(inner);
        return true;
    }

    static string StripQuotes(string s) {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return s.Substring(1, s.Length - 2);
        return s;
    }

    static string TryExpand(ProjectInstance instance, string s) {
        if (string.IsNullOrEmpty(s) || !s.Contains('$')) return s;
        try { return instance.ExpandString(s); } catch { return s; }
    }

    // SDK targets sometimes set `$(RestoreTaskAssemblyFile) = "NuGet.Build.Tasks.dll"` — a
    // bare filename, which MSBuild's task resolver searches against the running MSBuild's
    // assembly directory (the SDK root). Mirror that here for codegen-time discovery.
    static string? TryResolveOnSearchPath(ProjectInstance instance, string fileName) {
        if (string.IsNullOrEmpty(fileName)) return null;
        // Reject already-resolved absolute paths and anything that obviously has directories.
        if (Path.IsPathRooted(fileName)) return null;
        if (fileName.Contains('/') || fileName.Contains('\\')) return null;

        // Walk MSBuildToolsPath, MSBuildBinPath, MSBuildSDKsPath, NETCoreSdkVersion-derived
        // SDK directory, and the .NET install root. First hit wins.
        var probeDirs = new List<string>();
        void Add(string? p) { if (!string.IsNullOrEmpty(p) && !probeDirs.Contains(p)) probeDirs.Add(p); }
        Add(instance.GetPropertyValue("MSBuildToolsPath"));
        Add(instance.GetPropertyValue("MSBuildBinPath"));
        Add(instance.GetPropertyValue("MSBuildExtensionsPath"));
        // _NETCoreSdkDirectory holds the active SDK directory — where the
        // bare-filename task DLLs live.
        Add(instance.GetPropertyValue("_NETCoreSdkDirectory"));
        Add(instance.GetPropertyValue("MSBuildSDKsPath"));

        foreach (var dir in probeDirs) {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    static string? ShortName(string fullName) {
        if (string.IsNullOrEmpty(fullName)) return null;
        var dot = fullName.LastIndexOf('.');
        return dot < 0 ? fullName : fullName.Substring(dot + 1);
    }

    // MSBuild's intrinsic tasks — known by name without any <UsingTask> declaration in the
    // SDK XML. All live in Microsoft.Build.Tasks.Core.dll alongside Framework/Utilities.
    // We deliberately include only "pure data" intrinsics (no engine/cross-build/diagnostic
    // tasks). Excluded:
    //   - MSBuild — recursive cross-build invocation; real impl pulls in ProjectCollection
    //     and the full MSBuild evaluation engine (5+GB RAM, minutes of work). Our codegen
    //     emits this task in unused-by-HelloConsole code paths, so NotImplemented is fine.
    //   - MSBuildInternalMessage — emits structured SDK diagnostics; for HelloConsole this
    //     surfaces MSB3540 about MSBuildProjectExtensionsPath being set at the wrong time,
    //     a false positive given our two-stage initialization. Skip until we want the
    //     diagnostics for real.
    // Listing only the intrinsics HelloConsole's target graph actually hits; extend as
    // additional fixtures need them.
    static readonly (string ShortName, string FullTypeName)[] IntrinsicTaskMap = new[] {
        ("AssignTargetPath",      "Microsoft.Build.Tasks.AssignTargetPath"),
        ("RemoveDuplicates",      "Microsoft.Build.Tasks.RemoveDuplicates"),
        ("WriteCodeFragment",     "Microsoft.Build.Tasks.WriteCodeFragment"),
        ("FindAppConfigFile",     "Microsoft.Build.Tasks.FindAppConfigFile"),
        ("GetFrameworkPath",      "Microsoft.Build.Tasks.GetFrameworkPath"),
        ("AssignCulture",         "Microsoft.Build.Tasks.AssignCulture"),
        ("ResolveAssemblyReference", "Microsoft.Build.Tasks.ResolveAssemblyReference"),
        ("CopyRefAssembly",       "Microsoft.Build.Tasks.CopyRefAssembly"),
        ("CreateCSharpManifestResourceName", "Microsoft.Build.Tasks.CreateCSharpManifestResourceName"),
    };

    // Find Microsoft.Build.Tasks.Core.dll. Probe the same property set we use for
    // bare-filename UsingTask resolution; the intrinsics DLL is in the SDK root.
    static string? FindIntrinsicsAsmPath(ProjectInstance instance) {
        return TryResolveOnSearchPath(instance, "Microsoft.Build.Tasks.Core.dll");
    }
}

// v1.5 Phase 1b: load each resolved task DLL via MetadataLoadContext (NO JIT) and
// extract per-task type metadata. This is the input that drives Phase 3 codegen of
// typed `new TaskType { Prop = ... }` calls.
//
// MetadataLoadContext requires a PathAssemblyResolver seeded with the full closure
// of assemblies that the task DLLs might reference: Microsoft.Build.Framework,
// Microsoft.Build.Utilities.Core, the runtime ref pack (System.*), plus all the
// task DLLs themselves so cross-references between SDK tooling resolve.
static class TaskMetadataLoader {
    public sealed class TaskMeta {
        public required string TaskName;
        public required string FullTypeName;
        public required string AssemblyPath;
        public required List<PropertyMeta> Properties;     // public settable, ordered by name
        public required List<PropertyMeta> OutputProperties; // properties marked [Output] (subset that can also be in Properties when CanWrite, OR read-only output)
    }

    public sealed class PropertyMeta {
        public required string Name;
        public required string PropertyTypeName;   // CLR full type name; e.g. "System.String", "Microsoft.Build.Framework.ITaskItem[]"
        public required string PropertyTypeShort;  // e.g. "string", "ITaskItem[]"
        public required bool IsRequired;
        public required bool IsOutput;
        public required bool CanWrite;
    }

    public sealed class MetadataIndex {
        public required Dictionary<string, TaskMeta> ByTaskName;          // expanded task name (case-insensitive)
        public required HashSet<string> AmbiguousFullTypeNames;            // CLR full type names that exist in MULTIPLE referenced assemblies
        public required List<string> LoadErrors;
        public int TaskCount => ByTaskName.Count;
    }

    public static MetadataIndex Load(UsingTaskRegistry.Registry registry) {
        // Build the assembly probe list. Sources:
        //   - The set of resolved task DLLs themselves
        //   - The SDK directory containing Microsoft.Build.Framework / Utilities (one per
        //     unique task-DLL parent walked up to find the SDK root)
        //   - Current runtime ref pack (so System.* types resolve)
        var probeFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddDir(string? dir) {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.dll")) {
                var name = Path.GetFileNameWithoutExtension(f);
                if (!probeFiles.ContainsKey(name)) probeFiles[name] = f;
            }
        }

        // 1. Each task DLL and its sibling directory.
        var taskAsmPaths = registry.Entries
            .Where(e => e.ResolvedAssemblyPath != null && !e.IsInline)
            .Select(e => e.ResolvedAssemblyPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var p in taskAsmPaths) AddDir(Path.GetDirectoryName(p));

        // 2. Walk up to find the .NET SDK root (contains Microsoft.Build.Framework.dll
        //    directly, e.g. /usr/local/share/dotnet/sdk/11.0.100-preview.4.26230.115/).
        foreach (var p in taskAsmPaths) {
            var d = Path.GetDirectoryName(p);
            while (d != null) {
                if (File.Exists(Path.Combine(d, "Microsoft.Build.Framework.dll"))) {
                    AddDir(d);
                    AddDir(Path.Combine(d, "ref"));
                    break;
                }
                d = Path.GetDirectoryName(d);
            }
        }

        // 3. Runtime directory (for System.* resolution at metadata level).
        AddDir(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());

        // 4. The ref pack for the running framework — System.Runtime, System.Collections, etc.
        // Find it under DOTNET_ROOT/packs/Microsoft.NETCore.App.Ref/<version>/ref/net*/.
        var dotnetRoot = Path.GetDirectoryName(
            Path.GetDirectoryName(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()));
        if (dotnetRoot != null) {
            var refPackRoot = Path.Combine(Path.GetDirectoryName(dotnetRoot)!, "packs", "Microsoft.NETCore.App.Ref");
            if (Directory.Exists(refPackRoot)) {
                foreach (var ver in Directory.GetDirectories(refPackRoot)) {
                    var currentTfm = $"net{Environment.Version.Major}.0";
                    var currentRef = Directory.GetDirectories(Path.Combine(ver, "ref"), currentTfm).FirstOrDefault();
                    if (currentRef != null) { AddDir(currentRef); break; }
                }
            }
        }

        var resolver = new PathAssemblyResolver(probeFiles.Values);
        var ctx = new MetadataLoadContext(resolver);

        var byTaskName = new Dictionary<string, TaskMeta>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        // For each USED+resolved entry in the registry, load the assembly and find the type.
        // We process by assembly to avoid loading the same DLL twice. To honor MSBuild's
        // last-wins UsingTask semantics in the face of duplicates (same task name in two
        // different DLLs), we use only the effective binding from registry.ByTaskName.
        var effectiveEntries = registry.ByTaskName.Values
            .Where(e => e.IsUsed && e.ResolvedAssemblyPath != null && !e.IsInline)
            .Distinct();
        var entriesByAsm = effectiveEntries
            .GroupBy(e => e.ResolvedAssemblyPath!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in entriesByAsm) {
            System.Reflection.Assembly asm;
            try { asm = ctx.LoadFromAssemblyPath(group.Key); }
            catch (Exception ex) {
                errors.Add($"failed to load {group.Key}: {ex.Message}");
                continue;
            }

            foreach (var entry in group) {
                var fullName = entry.ExpandedTaskName;
                // Try exact full name first; if that's not a type in this DLL, try the simple
                // name (last segment) and scan all types in the assembly. Helpful for SDKs
                // that declare short TaskName but the actual type lives in a deeper namespace.
                var t = asm.GetType(fullName, throwOnError: false);
                if (t == null) {
                    var shortName = LastDotSegment(fullName);
                    t = asm.GetTypes().FirstOrDefault(x =>
                        x.IsClass && !x.IsAbstract && x.Name.Equals(shortName, StringComparison.Ordinal));
                }
                if (t == null) {
                    errors.Add($"task '{entry.ExpandedTaskName}': type not found in {Path.GetFileName(group.Key)}");
                    continue;
                }

                var props = new List<PropertyMeta>();
                var outProps = new List<PropertyMeta>();
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal)) {
                    // Skip well-known engine-injected properties (BuildEngine, BuildEngineN, HostObject, Log)
                    if (IsEngineInjected(p.Name)) continue;

                    var atts = p.GetCustomAttributesData();
                    bool isOutput   = atts.Any(a => (a.AttributeType.FullName ?? "") == "Microsoft.Build.Framework.OutputAttribute");
                    bool isRequired = atts.Any(a => (a.AttributeType.FullName ?? "") == "Microsoft.Build.Framework.RequiredAttribute");

                    var meta = new PropertyMeta {
                        Name = p.Name,
                        PropertyTypeName = p.PropertyType.FullName ?? p.PropertyType.Name,
                        PropertyTypeShort = ShortClrName(p.PropertyType),
                        IsRequired = isRequired,
                        IsOutput = isOutput,
                        CanWrite = p.SetMethod != null && p.SetMethod.IsPublic,
                    };
                    if (meta.CanWrite || meta.IsOutput) props.Add(meta);
                    if (meta.IsOutput) outProps.Add(meta);
                }

                byTaskName[fullName] = new TaskMeta {
                    TaskName = entry.ExpandedTaskName,
                    FullTypeName = t.FullName ?? t.Name,
                    AssemblyPath = group.Key,
                    Properties = props,
                    OutputProperties = outProps,
                };
            }
        }

        // Detect type-name collisions: two chosen task DLLs both export the same CLR full type
        // name. C# can't resolve `new Namespace.X()` without extern aliases (which we don't
        // emit), so we record these and fall back to NotImplemented when emitting.
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        var chosenAsmPaths = byTaskName.Values.Select(t => t.AssemblyPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var typeNameToAsms = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var path in chosenAsmPaths) {
            try {
                var asm = ctx.LoadFromAssemblyPath(path);
                foreach (var t in asm.GetExportedTypes()) {
                    if (!t.IsClass || t.IsAbstract) continue;
                    var key = t.FullName ?? "";
                    if (!typeNameToAsms.TryGetValue(key, out var set)) typeNameToAsms[key] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.Add(path);
                }
            } catch {
                // Tolerated — we already loaded what we needed for the task metadata above.
            }
        }
        foreach (var (typeName, asms) in typeNameToAsms)
            if (asms.Count > 1) ambiguous.Add(typeName);

        return new MetadataIndex {
            ByTaskName = byTaskName,
            AmbiguousFullTypeNames = ambiguous,
            LoadErrors = errors,
        };
    }

    static bool IsEngineInjected(string propName) =>
        propName == "BuildEngine" ||
        (propName.StartsWith("BuildEngine", StringComparison.Ordinal) && propName.Length > 11 && char.IsDigit(propName[11])) ||
        propName == "HostObject" || propName == "Log";

    static string LastDotSegment(string s) {
        if (string.IsNullOrEmpty(s)) return s;
        var dot = s.LastIndexOf('.');
        return dot < 0 ? s : s.Substring(dot + 1);
    }

    static string ShortClrName(Type t) {
        if (t.IsArray) return ShortClrName(t.GetElementType()!) + "[]";
        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1")
            return ShortClrName(t.GetGenericArguments()[0]) + "?";
        return t.Name switch {
            "String"   => "string",
            "Boolean"  => "bool",
            "Int32"    => "int",
            "Int64"    => "long",
            "Double"   => "double",
            _ => t.Name,
        };
    }
}

static class Emitter {
    static Dictionary<string, string> _props = new(StringComparer.Ordinal);
    static Dictionary<string, string> _items = new(StringComparer.Ordinal);
    static bool _hasCallTarget = false;
    static TaskMetadataLoader.MetadataIndex? _taskMeta;

    public static void SetRegistries(Dictionary<string, string> props, Dictionary<string, string> items) {
        _props = props; _items = items;
    }
    public static void SetCallTargetFlag(bool hasCallTarget) => _hasCallTarget = hasCallTarget;
    public static void SetTaskMetadata(TaskMetadataLoader.MetadataIndex meta) => _taskMeta = meta;
    public static bool TryCanonicalProp(string lower, out string canonical) => _props.TryGetValue(lower, out canonical!);
    public static bool TryCanonicalItem(string lower, out string canonical) => _items.TryGetValue(lower, out canonical!);

    // Tasks we keep hand-rolling rather than instantiating real CLR types for. Intrinsics
    // that are simpler/faster in our own code than going through the SDK task DLL closure.
    static readonly HashSet<string> _keepHandrolled = new(StringComparer.OrdinalIgnoreCase) {
        // File system intrinsics
        "Copy", "Touch", "Delete", "MakeDir", "RemoveDir",
        "WriteLinesToFile", "ReadLinesFromFile", "FindUnderPath", "ConvertToAbsolutePath",
        // Logging intrinsics
        "Message", "Warning", "Error",
        // Misc
        "Exec", "Hash",
        // CreateProperty/CreateItem/etc. handled below via KnownTasks branch
    };

    // Look up the task type metadata for a task invocation. Returns null if (a) we have no
    // metadata loaded yet (Phase 1b not run), (b) the task is in the hand-rolled keep set,
    // or (c) the task isn't registered in any UsingTask.
    public static TaskMetadataLoader.TaskMeta? GetTaskMeta(string taskName) {
        if (_taskMeta == null) return null;
        if (_keepHandrolled.Contains(taskName)) return null;
        if (_taskMeta.ByTaskName.TryGetValue(taskName, out var m)) {
            // Skip tasks whose full type name is multiply-defined across the referenced
            // assembly closure. C# can't disambiguate without extern aliases (which we don't
            // emit yet); fall back to NotImplemented. None of these are critical for
            // HelloConsole — they're NuGet Pack/Restore-side tasks that don't run at build time.
            if (_taskMeta.AmbiguousFullTypeNames.Contains(m.FullTypeName)) return null;
            return m;
        }
        // Try a short-name fallback: scan metadata index for a type whose simple name matches.
        foreach (var kv in _taskMeta.ByTaskName)
            if (LastSegment(kv.Key).Equals(taskName, StringComparison.OrdinalIgnoreCase)) {
                if (_taskMeta.AmbiguousFullTypeNames.Contains(kv.Value.FullTypeName)) return null;
                return kv.Value;
            }
        return null;
    }
    static string LastSegment(string s) {
        var dot = s.LastIndexOf('.');
        return dot < 0 ? s : s.Substring(dot + 1);
    }

    public static void Emit(StringBuilder sb, Microsoft.Build.Evaluation.Project project, ProjectInstance instance, List<string> sequence, string entryTarget) {
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by bsharp codegen. Static fields + R2R/JIT in-proc SDK task loading.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1998");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Diagnostics;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using System.Runtime.Loader;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using System.Xml;");
        sb.AppendLine("using Bsharp.Generated.TaskModel;");
        sb.AppendLine();

        // Main
        sb.AppendLine("""
var sw = Stopwatch.StartNew();
var restoreElapsed = TimeSpan.Zero;
string command = "build";
bool noBuild = false;
bool noRestore = false;
string? csprojArg = null;
for (int i = 0; i < args.Length; i++) {
    var a = args[i];
    if (a == "build" || a == "run") command = a;
    else if (a == "--no-build") noBuild = true;
    else if (a == "--no-restore") noRestore = true;
    else if ((a == "-v" || a == "--verbosity") && i + 1 < args.Length) Log.Level = Log.Parse(args[++i]);
    else if (a.StartsWith("-v:", StringComparison.Ordinal)) Log.Level = Log.Parse(a.Substring(3));
    else if (a.StartsWith("--verbosity:", StringComparison.Ordinal)) Log.Level = Log.Parse(a.Substring(12));
    else if (a.EndsWith(".csproj", StringComparison.Ordinal)) csprojArg = a;
}
string csprojPath = csprojArg != null
    ? Path.GetFullPath(csprojArg)
    : GeneratedProjectInfo.ProjectPath;

var preInitFastNoOp = !noBuild && FastNoOpBuildBeforePopulate(csprojPath);
if (!preInitFastNoOp) {
    try {
        InitialState.Populate(csprojPath);
    } catch (Exception ex) {
        Console.Error.WriteLine($"FAIL during init: {ex.GetType().Name}: {ex.Message}");
        if (Environment.GetEnvironmentVariable("BSHARP_TRACE") == "1") Console.Error.WriteLine(ex.StackTrace);
        return 2;
    }
}

if (!noBuild) {
    var fastNoOp = preInitFastNoOp || FastNoOpBuild(csprojPath);
    if (!fastNoOp) {
        if (!noRestore && NeedsRestore(csprojPath)) {
            var restoreSw = Stopwatch.StartNew();
            var restoreRc = RunRestore(csprojPath);
            restoreSw.Stop();
            restoreElapsed = restoreSw.Elapsed;
            if (restoreRc != 0)
                return restoreRc;
        }
        Targets.Build().GetAwaiter().GetResult();

        // Post-build fallback: if GenerateBuildRuntimeConfigurationFiles failed (it needs
        // project.assets.json from a NuGet restore which we skip) and we have a built dll,
        // hand-write a minimal runtimeconfig.json so the binary is actually runnable.
        // For HelloConsole this is sufficient; projects with framework references beyond
        // Microsoft.NETCore.App need a richer config.
        try {
            // Normalize path separators — some baked SDK properties use literal `\`.
            // P.TargetPath is the expected location; if Csc wrote to a sibling location
            // with `\` in the path, we look there too and use whichever exists.
            string FindBuilt(string expected) {
                var candidates = new List<string>();
                if (File.Exists(expected)) candidates.Add(expected);
                var swapped = expected.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                if (File.Exists(swapped)) candidates.Add(swapped);
                // Last resort: search obj/<config>/<tfm>/ and obj\<config>/<tfm>/ for the filename.
                var projectDir = Path.GetDirectoryName(P.MSBuildProjectFullPath)!;
                var fileName = Path.GetFileName(expected);
                foreach (var candidate in Directory.EnumerateFiles(projectDir, fileName, SearchOption.AllDirectories)) {
                    var normalized = candidate.Replace('\\', '/');
                    if (!normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (normalized.Contains("/ref/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (normalized.Contains("/refint/", StringComparison.OrdinalIgnoreCase)) continue;
                    candidates.Add(candidate);
                }
                return candidates.Count == 0
                    ? expected
                    : candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }
            string DefaultRuntimeFrameworkVersion() {
                var description = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
                var lastSpace = description.LastIndexOf(' ');
                if (lastSpace >= 0 && lastSpace + 1 < description.Length) {
                    var version = description[(lastSpace + 1)..];
                    if (version.Length > 0 && char.IsDigit(version[0])) return version;
                }
                var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var runtimeVersion = Path.GetFileName(runtimeDir);
                return !string.IsNullOrEmpty(runtimeVersion) && char.IsDigit(runtimeVersion[0]) ? runtimeVersion : "11.0.0";
            }
            var dll = string.IsNullOrEmpty(P.TargetPath) ? "" : FindBuilt(P.TargetPath);
            if (!string.IsNullOrEmpty(dll) && File.Exists(dll)) {
                var configPath = Path.ChangeExtension(dll, ".runtimeconfig.json");
                if (!File.Exists(configPath)) {
                    var tfm = string.IsNullOrEmpty(P.TargetFramework) ? "net11.0" : P.TargetFramework;
                    var rfv = string.IsNullOrEmpty(P.RuntimeFrameworkVersion) ? DefaultRuntimeFrameworkVersion() : P.RuntimeFrameworkVersion;
                    File.WriteAllText(configPath,
                        "{\n" +
                        "  \"runtimeOptions\": {\n" +
                        $"    \"tfm\": \"{tfm}\",\n" +
                        "    \"framework\": {\n" +
                        "      \"name\": \"Microsoft.NETCore.App\",\n" +
                        $"      \"version\": \"{rfv}\"\n" +
                        "    }\n" +
                        "  }\n" +
                        "}\n");
                }
                // Also copy the dll to bin/<config>/<tfm>/ so `dotnet <project>` finds it.
                // GenerateBuildRuntimeConfigurationFiles + CopyFilesToOutputDirectory normally do this.
                var outDirProp = P.OutputPath;
                if (!string.IsNullOrEmpty(outDirProp)) {
                    var projectDir = Path.GetDirectoryName(P.MSBuildProjectFullPath)!;
                    var outDir = Path.IsPathRooted(outDirProp)
                        ? outDirProp
                        : Path.Combine(projectDir, outDirProp.Replace('\\', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(outDir);
                    var destDll = Path.Combine(outDir, Path.GetFileName(dll));
                    var destConfig = Path.Combine(outDir, Path.GetFileName(configPath));
                    if (!File.Exists(destDll) || File.GetLastWriteTimeUtc(destDll) < File.GetLastWriteTimeUtc(dll))
                        File.Copy(dll, destDll, overwrite: true);
                    if (!File.Exists(destConfig) || File.GetLastWriteTimeUtc(destConfig) < File.GetLastWriteTimeUtc(configPath))
                        File.Copy(configPath, destConfig, overwrite: true);
                    // Also copy pdb if present.
                    var pdb = Path.ChangeExtension(dll, ".pdb");
                    if (File.Exists(pdb)) {
                        var destPdb = Path.Combine(outDir, Path.GetFileName(pdb));
                        if (!File.Exists(destPdb) || File.GetLastWriteTimeUtc(destPdb) < File.GetLastWriteTimeUtc(pdb))
                            File.Copy(pdb, destPdb, overwrite: true);
                    }
                }
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"bsharp: post-build runtimeconfig/copy step failed: {ex.Message}");
        }
    }
    sw.Stop();

    List<(string target, string error)>? critical = null;
    foreach (var e in Targets.Errors)
        if (IsCriticalBuildTarget(e.target))
            (critical ??= new()).Add(e);
    var criticalCount = critical?.Count ?? 0;
    var ignored = Targets.Errors.Count - criticalCount;
    if (Log.Level >= Log.Verbosity.Minimal)
        WriteBuildSummary(sw.Elapsed, restoreElapsed, criticalCount, ignored);
    if (criticalCount > 0) {
        Console.Error.WriteLine();
        Console.Error.WriteLine("=== Critical build errors ===");
        foreach (var (t, msg) in critical!) Console.Error.WriteLine($"  {t}: {msg}");
        return 1;
    }
}

if (command == "run") {
    if (!noBuild && Log.Level >= Log.Verbosity.Minimal)
        Console.WriteLine("---");
    var targetPath = P.TargetPath;
    if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath)) {
        Console.Error.WriteLine($"FAIL: cannot run, TargetPath not found: '{targetPath}'");
        return 2;
    }
    var psi = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false };
    psi.ArgumentList.Add("exec");
    psi.ArgumentList.Add(targetPath);
    var proc = System.Diagnostics.Process.Start(psi)!;
    proc.WaitForExit();
    return proc.ExitCode;
}
return 0;

static void WriteBuildSummary(TimeSpan elapsed, TimeSpan restoreElapsed, int criticalCount, int ignored) {
    var buildMs = elapsed.TotalMilliseconds;
    var restoreMs = restoreElapsed.TotalMilliseconds;
    var taskMs = Log.CumulativeTaskMilliseconds;
    WriteMetric("build time", buildMs);
    if (restoreMs > 0)
        WriteMetric("restore time", restoreMs);
    WriteMetric("cumulative tasks", taskMs);
    WriteMetric("net overhead", buildMs - restoreMs - taskMs);
    if (criticalCount != 0 || (ignored > 0 && Log.Level >= Log.Verbosity.Detailed)) {
        Console.Write("errors: ");
        Console.Write(criticalCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (ignored > 0 && Log.Level >= Log.Verbosity.Detailed) {
            Console.Write(" (ignored: ");
            Console.Write(ignored.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Console.Write(')');
        }
        Console.WriteLine();
    }
}

static void WriteMetric(string name, double value) {
    Console.Write(name);
    Console.Write(": ");
    Console.Write(value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    Console.WriteLine("ms");
}

static bool NeedsRestore(string csprojPath) {
    var assets = ResolveProjectPath(Path.GetDirectoryName(csprojPath)!, P.ProjectAssetsFile);
    if (string.IsNullOrEmpty(assets))
        assets = Path.Combine(Path.GetDirectoryName(csprojPath)!, "obj", "project.assets.json");
    if (!File.Exists(assets)) return true;
    var assetsTime = File.GetLastWriteTimeUtc(assets);
    if (File.GetLastWriteTimeUtc(csprojPath) > assetsTime) return true;
    var projectDir = Path.GetDirectoryName(csprojPath)!;
    foreach (var fileName in new[] { "Directory.Packages.props", "Directory.Build.props", "Directory.Build.targets", "NuGet.config", "global.json" }) {
        for (var dir = projectDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)) {
            var path = Path.Combine(dir, fileName);
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > assetsTime)
                return true;
        }
    }
    return false;
}

static int RunRestore(string csprojPath) {
    var errorStart = Targets.Errors.Count;
    Targets.Restore().GetAwaiter().GetResult();
    var restoreErrors = Targets.Errors.Skip(errorStart).ToArray();
    if (restoreErrors.Length > 0) {
        Console.Error.WriteLine();
        Console.Error.WriteLine("=== Restore errors ===");
        foreach (var (t, msg) in restoreErrors) Console.Error.WriteLine($"  {t}: {msg}");
        return 1;
    }
    Log.ResetTaskTiming();
    return 0;
}

static bool IsCriticalBuildTarget(string name) {
    if (name.StartsWith("_GenerateRestore", StringComparison.Ordinal)) return false;
    if (name.StartsWith("_GetRestore", StringComparison.Ordinal)) return false;
    if (name.StartsWith("Restore", StringComparison.Ordinal)) return false;
    if (name.Contains("RestoreGraph", StringComparison.Ordinal)) return false;
    return name switch {
        "CollectPackageReferences" => false,
        "CheckForImplicitPackageReferenceOverrides" => false,
        "_CheckForObsoleteDotNetCliToolReferences" => false,
        "CollectFrameworkReferences" => false,
        "AddPrunePackageReferences" => false,
        "_CollectTargetFrameworkForTelemetry" => false,
        "InitializeSourceControlInformationFromSourceControlManager" => false,
        "SetEmbeddedFilesFromSourceControlManagerUntrackedFiles" => false,
        "SourceLinkHasSingleProvider" => false,
        "_SourceLinkHasSingleProvider" => false,
        "GetSourceLinkUrl" => false,
        "TranslateRepositoryUrls" => false,
        "GenerateSourceLinkFile" => false,
        "_GenerateSourceLinkFile" => false,
        "_GetAllRestoreProjectPathItems" => false,
        "_FilterRestoreGraphProjectInputItems" => false,
        "ResolvePackageAssets" => false,
        "GenerateBuildDependencyFile" => false,
        "GenerateBuildRuntimeConfigurationFiles" => false,
        _ => true,
    };
}

static bool FastNoOpBuild(string csprojPath) {
    var projectDir = Path.GetDirectoryName(csprojPath)!;
    var targetPath = ResolveProjectPath(projectDir, P.TargetPath);
    if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
        return false;
    var runtimeConfig = Path.ChangeExtension(targetPath, ".runtimeconfig.json");
    if (!File.Exists(runtimeConfig))
        return false;

    var outputTime = File.GetLastWriteTimeUtc(targetPath);
    foreach (var input in FastNoOpInputs(projectDir, csprojPath)) {
        if (!File.Exists(input)) continue;
        if (File.GetLastWriteTimeUtc(input) > outputTime)
            return false;
    }
    return true;
}

static bool FastNoOpBuildBeforePopulate(string csprojPath) {
    if (GeneratedProjectInfo.HasCompilerExtensionInputs)
        return false;
    var projectDir = string.Equals(csprojPath, GeneratedProjectInfo.ProjectPath, StringComparison.OrdinalIgnoreCase)
        ? GeneratedProjectInfo.ProjectDirectory
        : Path.GetDirectoryName(csprojPath)!;
    var targetPath = ResolveProjectPath(projectDir, P.TargetPath);
    if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
        return false;
    var runtimeConfig = Path.ChangeExtension(targetPath, ".runtimeconfig.json");
    if (!File.Exists(runtimeConfig))
        return false;

    var outputTime = File.GetLastWriteTimeUtc(targetPath);
    if (IsInputNewerThanOutput(csprojPath, outputTime))
        return false;
    if (FastPathFileHelpers.HasProjectSourceNewerThanOutput(projectDir, outputTime))
        return false;
    if (FastPathFileHelpers.HasShapeInputNewerThanOutput(projectDir, outputTime))
        return false;
    return true;
}

static bool IsInputNewerThanOutput(string input, DateTime outputTime) =>
    File.Exists(input) && File.GetLastWriteTimeUtc(input) > outputTime;

static IEnumerable<string> FastNoOpInputs(string projectDir, string csprojPath) {
    yield return csprojPath;
    foreach (var compile in I.Get("compile")) {
        var path = ResolveProjectPath(projectDir, compile.Identity);
        if (!string.IsNullOrEmpty(path))
            yield return path;
    }
    foreach (var input in CompilerExtensionInputs(projectDir))
        yield return input;

    foreach (var input in FastNoOpShapeInputs(projectDir))
        yield return input;
}

static IEnumerable<string> CompilerExtensionInputs(string projectDir) {
    foreach (var item in I.Get("analyzer")) {
        var path = ResolveProjectPath(projectDir, item.Identity);
        if (!string.IsNullOrEmpty(path))
            yield return path;
    }
    foreach (var item in I.Get("additionalfiles")) {
        var path = ResolveProjectPath(projectDir, item.Identity);
        if (!string.IsNullOrEmpty(path))
            yield return path;
    }
    foreach (var item in I.Get("editorconfigfiles")) {
        var path = ResolveProjectPath(projectDir, item.Identity);
        if (!string.IsNullOrEmpty(path))
            yield return path;
    }
}

static IEnumerable<string> FastNoOpShapeInputs(string projectDir) {
    for (var dir = projectDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)) {
        var props = Path.Combine(dir, "Directory.Build.props");
        if (File.Exists(props)) yield return props;
        var targets = Path.Combine(dir, "Directory.Build.targets");
        if (File.Exists(targets)) yield return targets;
        var packages = Path.Combine(dir, "Directory.Packages.props");
        if (File.Exists(packages)) yield return packages;
        var globalJson = Path.Combine(dir, "global.json");
        if (File.Exists(globalJson)) yield return globalJson;
    }
}

static string ResolveProjectPath(string projectDir, string path) {
    if (string.IsNullOrEmpty(path)) return "";
    if (Path.DirectorySeparatorChar != '\\' && path.Contains('\\'))
        path = path.Replace('\\', Path.DirectorySeparatorChar);
    return Path.IsPathRooted(path) ? path : Path.Combine(projectDir, path);
}

static class FastPathFileHelpers {
    public static IEnumerable<string> EnumerateProjectSourceFiles(string projectDir) {
        var pending = new Stack<string>();
        pending.Push(projectDir);
        while (pending.Count > 0) {
            var dir = pending.Pop();
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs"))
                yield return f;

            foreach (var subdir in Directory.EnumerateDirectories(dir)) {
                var name = Path.GetFileName(subdir);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals(".bsharp", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push(subdir);
            }
        }
    }

    public static bool HasProjectSourceNewerThanOutput(string projectDir, DateTime outputTime) {
        var pending = new Stack<string>();
        pending.Push(projectDir);
        while (pending.Count > 0) {
            var dir = pending.Pop();
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs"))
                if (File.GetLastWriteTimeUtc(f) > outputTime)
                    return true;

            foreach (var subdir in Directory.EnumerateDirectories(dir)) {
                var name = Path.GetFileName(subdir);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals(".bsharp", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push(subdir);
            }
        }
        return false;
    }

    public static bool HasShapeInputNewerThanOutput(string projectDir, DateTime outputTime) {
        for (var dir = projectDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)) {
            if (IsNewer(Path.Combine(dir, "Directory.Build.props"), outputTime)) return true;
            if (IsNewer(Path.Combine(dir, "Directory.Build.targets"), outputTime)) return true;
            if (IsNewer(Path.Combine(dir, "Directory.Packages.props"), outputTime)) return true;
            if (IsNewer(Path.Combine(dir, "global.json"), outputTime)) return true;
        }
        return false;
    }

    static bool IsNewer(string path, DateTime outputTime) =>
        File.Exists(path) && File.GetLastWriteTimeUtc(path) > outputTime;
}
""");
        sb.AppendLine();
        var hasCompilerExtensionInputs =
            project.GetItems("Analyzer").Any() ||
            project.GetItems("AdditionalFiles").Any() ||
            project.GetItems("EditorConfigFiles").Any();
        sb.AppendLine($"static class GeneratedProjectInfo {{ public const string ProjectPath = {CSharpLiteral(project.FullPath)}; public const string ProjectDirectory = {CSharpLiteral(Path.GetDirectoryName(project.FullPath) ?? "")}; public const bool HasCompilerExtensionInputs = {(hasCompilerExtensionInputs ? "true" : "false")}; }}");
        sb.AppendLine();

        EmitItemAndRuntimeHelpers(sb);
        EmitTaskHostRegistry(sb);
        EmitP(sb, project);
        EmitI(sb, project);
        EmitTasks(sb);
        EmitInitialState(sb, project);
        ResetGeneratedTaskHelpers();
        EmitTargets(sb, instance, sequence, entryTarget);
        EmitGeneratedTaskHelpers(sb);
    }

    static void EmitItemAndRuntimeHelpers(StringBuilder sb) {
        sb.AppendLine("""
class Item {
    public string Identity;
    Dictionary<string, string>? _m;
    public Dictionary<string, string> M => _m ??= new Dictionary<string, string>(StringComparer.Ordinal);
    public Dictionary<string, string>? MetadataOrNull => _m;
    public Item(string id) {
        Identity = id;
    }
    public Item(string id, Dictionary<string, string> metadata) {
        Identity = id;
        _m = metadata.Count == 0 ? null : metadata;
    }
    public string GetMetadata(string name) => name switch {
        "identity"    => Identity,
        "fullpath"    => Path.GetFullPath(Identity),
        "filename"    => Path.GetFileNameWithoutExtension(Identity),
        "extension"   => Path.GetExtension(Identity),
        "directory"   => Path.GetDirectoryName(Identity) ?? "",
        "relativedir" => RelativeDir(Identity),
        "rootdir"     => Path.GetPathRoot(Identity) ?? "",
        _ => _m != null && _m.TryGetValue(name, out var v) ? v : "",
    };
    // True iff this item's metadata `name` (lowercase) equals `value` case-insensitively.
    // Cheaper than the equivalent string.Equals(GetMetadata("name"), value, OrdinalIgnoreCase)
    // pattern that conditions / item filters used to generate.
    public bool HasMetadata(string name, string value) =>
        string.Equals(GetMetadata(name), value, StringComparison.OrdinalIgnoreCase);
    public void SetMetadata(string name, string value) => M[name.ToLowerInvariant()] = value ?? "";
    public void CopyMetadataTo(Item destination) {
        if (_m == null) return;
        foreach (var kv in _m) destination.M[kv.Key] = kv.Value;
    }
    static string RelativeDir(string id) {
        var d = Path.GetDirectoryName(id);
        return string.IsNullOrEmpty(d) ? "" : d + Path.DirectorySeparatorChar;
    }
}

// Real SDK tasks run out-of-proc in the persistent CoreCLR task server. The main
// generated host can stay NativeAOT and avoid rooting dynamic task-loading machinery.
static partial class TaskRunner {
    public sealed record TaskDescriptor(string ShortName, string FullTypeName, string AssemblyPath, string[] OutputNames);

    static readonly Dictionary<string, TaskDescriptor> _tasks = new(StringComparer.OrdinalIgnoreCase);
    static string _subCliDir = "";
    static TaskServerClient? _server;

    static TaskRunner() => RegisterTasks();
    static partial void RegisterTasks();

    public static void Init(string subCliDir) => _subCliDir = subCliDir;

    static void Add(string shortName, string fullTypeName, string assemblyPath, string[] outputNames) =>
        _tasks[shortName] = new TaskDescriptor(shortName, fullTypeName, assemblyPath, outputNames);

    public sealed class TaskInstance {
        internal required TaskDescriptor Desc;
        internal required long StartedTicks;
        internal required Bsharp.Generated.TaskModel.TaskInvocation Invocation;
        internal Bsharp.Generated.TaskModel.TaskResult? Result;
    }

    public static TaskInstance Create(string taskShortName) {
        if (!_tasks.TryGetValue(taskShortName, out var desc))
            throw new FileNotFoundException($"task '{taskShortName}' is not registered for task-server execution");

        return new TaskInstance {
            Desc = desc,
            StartedTicks = Log.TaskStarted(desc.ShortName),
            Invocation = new Bsharp.Generated.TaskModel.TaskInvocation { TaskName = desc.ShortName }
        };
    }

    public static void SetString(TaskInstance task, string name, string value) => task.Invocation.SetString(name, value);
    public static void SetBool(TaskInstance task, string name, bool value) => task.Invocation.SetBool(name, value);
    public static void SetBool(TaskInstance task, string name, string value) {
        SetBool(task, name, string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }
    public static void SetInt(TaskInstance task, string name, int value) => task.Invocation.SetInt(name, value);
    public static void SetLong(TaskInstance task, string name, long value) => task.Invocation.SetLong(name, value);
    public static void SetDouble(TaskInstance task, string name, double value) => task.Invocation.SetDouble(name, value);
    public static void SetStrings(TaskInstance task, string name, string[] value) => task.Invocation.SetStrings(name, value);
    public static void SetStringIfNotNullOrEmpty(TaskInstance task, string name, string value) {
        if (!string.IsNullOrEmpty(value)) SetString(task, name, value);
    }
    public static void SetBoolIfNotNullOrEmpty(TaskInstance task, string name, string value) {
        if (!string.IsNullOrEmpty(value)) SetBool(task, name, value);
    }
    public static void SetIntIfNotNullOrEmpty(TaskInstance task, string name, string value) {
        if (!string.IsNullOrEmpty(value)) SetInt(task, name, BsharpInt.ToInt32(value));
    }
    public static void SetLongIfNotNullOrEmpty(TaskInstance task, string name, string value) {
        if (!string.IsNullOrEmpty(value)) SetLong(task, name, BsharpInt.ToInt64(value));
    }
    public static void SetDoubleIfNotNullOrEmpty(TaskInstance task, string name, string value) {
        if (!string.IsNullOrEmpty(value)) SetDouble(task, name, BsharpInt.ToDouble(value));
    }
    public static void SetStringsIfNotNullOrEmpty(TaskInstance task, string name, string value) {
        if (!string.IsNullOrEmpty(value)) SetStrings(task, name, StringList.FromSemicolonList(value));
    }
    public static void SetItem(TaskInstance task, string name, Bsharp.Generated.TaskModel.ItemSpec? value) {
        task.Invocation.SetItem(name, value);
    }
    public static void SetItemFromString(TaskInstance task, string name, string identity) {
        if (!string.IsNullOrEmpty(identity))
            SetItem(task, name, new Bsharp.Generated.TaskModel.ItemSpec { Identity = identity });
    }
    public static void SetItems(TaskInstance task, string name, Bsharp.Generated.TaskModel.ItemSpec[] value) {
        task.Invocation.SetItems(name, value);
    }

    public static string? Execute(TaskInstance task) {
        try {
            return ExecuteCore(task);
        } finally {
            Log.TaskFinished(task.Desc.ShortName, task.StartedTicks);
        }
    }

    static string? ExecuteCore(TaskInstance task) {
        try {
            var timestampGuard = CaptureTimestampGuard(task);
            task.Result = GetTaskServer().Invoke(task.Invocation);
            RestoreTimestampIfContentUnchanged(timestampGuard);
            return task.Result.Success ? null : task.Result.Error ?? $"task '{task.Desc.ShortName}' returned false";
        } catch (Exception ex) {
            return ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace;
        }
    }

    public static void ExecuteAndThrowOnError(TaskInstance task) {
        var error = Execute(task);
        if (error != null) throw new InvalidOperationException(error);
    }

    public static string GetString(TaskInstance task, string name) {
        return task.Result != null ? task.Result.GetString(name) : "";
    }

    public static string[] GetStrings(TaskInstance task, string name) {
        return task.Result != null ? task.Result.GetStrings(name) : Array.Empty<string>();
    }

    public static Bsharp.Generated.TaskModel.ItemSpec[] GetItems(TaskInstance task, string name) {
        return task.Result != null ? task.Result.GetItems(name) : Array.Empty<Bsharp.Generated.TaskModel.ItemSpec>();
    }

    static (string Path, byte[] Content, DateTime TimestampUtc)? CaptureTimestampGuard(TaskInstance task) {
        var path = task.Desc.ShortName switch {
            "WriteCodeFragment" => task.Invocation.GetString("OutputFile"),
            "GenerateMSBuildEditorConfig" => task.Invocation.GetString("File"),
            _ => "",
        };
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        return (path, File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
    }

    static void RestoreTimestampIfContentUnchanged((string Path, byte[] Content, DateTime TimestampUtc)? guard) {
        if (guard is not { } g || !File.Exists(g.Path))
            return;
        var current = File.ReadAllBytes(g.Path);
        if (current.AsSpan().SequenceEqual(g.Content))
            File.SetLastWriteTimeUtc(g.Path, g.TimestampUtc);
    }

    static TaskServerClient GetTaskServer() => _server ??= new TaskServerClient(ResolveTaskServerPath());

    static string ResolveTaskServerPath() {
        var env = Environment.GetEnvironmentVariable("BSHARP_TASK_SERVER_PATH");
        if (!string.IsNullOrEmpty(env)) return env;
        var exe = OperatingSystem.IsWindows() ? "BsharpTaskServer.exe" : "BsharpTaskServer";
        return Path.Combine(_subCliDir, "server", exe);
    }

    sealed class TaskServerClient {
        readonly string _path;
        Process? _process;
        Stream? _stdin;
        Stream? _stdout;
        readonly object _sync = new();
        public TaskServerClient(string path) => _path = path;

        public Bsharp.Generated.TaskModel.TaskResult Invoke(Bsharp.Generated.TaskModel.TaskInvocation invocation) {
            lock (_sync) {
                EnsureStarted();
                var payload = JsonSerializer.SerializeToUtf8Bytes(invocation, Bsharp.Generated.TaskModel.TaskModelJson.Default.TaskInvocation);
                WriteFrame(_stdin!, payload);
                var response = ReadFrame(_stdout!) ?? throw new EndOfStreamException("task server exited before writing a response");
                return JsonSerializer.Deserialize(response, Bsharp.Generated.TaskModel.TaskModelJson.Default.TaskResult)
                    ?? new Bsharp.Generated.TaskModel.TaskResult { Success = false, Error = "task server returned an empty response" };
            }
        }

        void EnsureStarted() {
            if (_process is { HasExited: false }) return;
            if (!File.Exists(_path)) throw new FileNotFoundException("task server executable not found", _path);
            var psi = new ProcessStartInfo(_path) {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            _process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start task server");
            _stdin = _process.StandardInput.BaseStream;
            _stdout = _process.StandardOutput.BaseStream;
        }

        static void WriteFrame(Stream output, byte[] payload) {
            Span<byte> lenBytes = stackalloc byte[4];
            BitConverter.TryWriteBytes(lenBytes, payload.Length);
            output.Write(lenBytes);
            output.Write(payload);
            output.Flush();
        }

        static byte[]? ReadFrame(Stream input) {
            Span<byte> lenBytes = stackalloc byte[4];
            var read = input.Read(lenBytes);
            if (read == 0) return null;
            while (read < 4) {
                var n = input.Read(lenBytes[read..]);
                if (n == 0) throw new EndOfStreamException();
                read += n;
            }
            var len = BitConverter.ToInt32(lenBytes);
            if (len < 0 || len > 64 * 1024 * 1024) throw new InvalidDataException($"invalid task-server frame length {len}");
            var payload = new byte[len];
            var offset = 0;
            while (offset < len) {
                var n = input.Read(payload, offset, len - offset);
                if (n == 0) throw new EndOfStreamException();
                offset += n;
            }
            return payload;
        }
    }
}

// Numeric parsing helpers — used when codegen needs to convert a string MSBuild value
// into an int/long/double for a typed task parameter. Outlined so call sites stay
// readable (no `out var value` collisions in the same scope).
static class BsharpInt {
    public static int    ToInt32(string s)  => int.TryParse(s,    out var v) ? v : 0;
    public static long   ToInt64(string s)  => long.TryParse(s,   out var v) ? v : 0L;
    public static double ToDouble(string s) => double.TryParse(s, out var v) ? v : 0d;
}

// Item ↔ ItemSpec conversion. Used by codegen-emitted real SDK task invocation
// to move item identity + metadata through the in-proc task model.
static class ItemSerde {
    public static Bsharp.Generated.TaskModel.ItemSpec OneSpec(Item it) {
        var spec = new Bsharp.Generated.TaskModel.ItemSpec { Identity = it.Identity };
        if (it.MetadataOrNull is { Count: > 0 } metadata) spec.Metadata = new Dictionary<string, string>(metadata);
        return spec;
    }
    public static Bsharp.Generated.TaskModel.ItemSpec[] ToSpecs(List<Item> items) {
        if (items.Count == 0) return Array.Empty<Bsharp.Generated.TaskModel.ItemSpec>();
        var arr = new Bsharp.Generated.TaskModel.ItemSpec[items.Count];
        for (int i = 0; i < items.Count; i++) arr[i] = OneSpec(items[i]);
        return arr;
    }
    public static Bsharp.Generated.TaskModel.ItemSpec[] ToSpecsWithIdentity(IEnumerable<Item> items, Func<Item, string> identitySelector) {
        var specs = new List<Bsharp.Generated.TaskModel.ItemSpec>();
        foreach (var item in items) {
            var spec = OneSpec(item);
            spec.Identity = identitySelector(item);
            specs.Add(spec);
        }
        return specs.ToArray();
    }
    public static Bsharp.Generated.TaskModel.ItemSpec[] SpecsFromScalar(string semicolonList) {
        if (string.IsNullOrEmpty(semicolonList)) return Array.Empty<Bsharp.Generated.TaskModel.ItemSpec>();
        var specs = new List<Bsharp.Generated.TaskModel.ItemSpec>();
        foreach (var part in new SemicolonSplit(semicolonList))
            specs.Add(new Bsharp.Generated.TaskModel.ItemSpec { Identity = part.ToString() });
        return specs.ToArray();
    }
    public static List<Item> FromSpecs(Bsharp.Generated.TaskModel.ItemSpec[] specs) {
        var list = new List<Item>(specs.Length);
        foreach (var s in specs) {
            var it = new Item(s.Identity);
            if (s.Metadata != null) foreach (var kv in s.Metadata) it.SetMetadata(kv.Key, kv.Value);
            list.Add(it);
        }
        return list;
    }
}

static class StringList {
    public static string[] FromSemicolonList(string value) {
        if (string.IsNullOrEmpty(value)) return Array.Empty<string>();
        var items = new List<string>();
        foreach (var part in new SemicolonSplit(value))
            items.Add(part.ToString());
        return items.ToArray();
    }
}

sealed class StringSet {
    readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _patterns = new();

    public static StringSet FromSemicolonList(string value) {
        var set = new StringSet();
        foreach (var part in new SemicolonSplit(value)) {
            set.Add(part.ToString());
        }
        return set;
    }

    public static StringSet FromItems(IEnumerable<Item> items) {
        var set = new StringSet();
        foreach (var item in items)
            set.Add(item.Identity);
        return set;
    }

    void Add(string value) {
        var item = Normalize(value);
        if (item.Contains('*') || item.Contains('?')) {
            _patterns.Add(item);
            if (item.Contains("/**/", StringComparison.Ordinal))
                _patterns.Add(item.Replace("/**/", "/", StringComparison.Ordinal));
        } else {
            _exact.Add(item);
        }
    }

    public bool Contains(string value) {
        value = Normalize(value);
        if (_exact.Contains(value)) return true;
        foreach (var pattern in _patterns)
            if (GlobMatch(pattern, value, 0, 0))
                return true;
        return false;
    }

    static string Normalize(string value) => value.Replace('\\', '/');

    static bool GlobMatch(string pattern, string value, int p, int v) {
        while (p < pattern.Length) {
            var c = pattern[p];
            if (c == '*') {
                while (p + 1 < pattern.Length && pattern[p + 1] == '*') p++;
                if (p + 1 == pattern.Length) return true;
                for (var i = v; i <= value.Length; i++)
                    if (GlobMatch(pattern, value, p + 1, i))
                        return true;
                return false;
            }
            if (v >= value.Length) return false;
            if (c == '?') {
                p++;
                v++;
                continue;
            }
            if (char.ToUpperInvariant(c) != char.ToUpperInvariant(value[v]))
                return false;
            p++;
            v++;
        }
        return v == value.Length;
    }
}

static class CondHelpers {
    public static int NumericCompare(string a, string b) {
        if (long.TryParse(a, out var x) && long.TryParse(b, out var y)) return x.CompareTo(y);
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)) return va.CompareTo(vb);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAny(string value, params string[] candidates) {
        foreach (var candidate in candidates)
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

static class TargetFrameworkHelpers {
    // Very lite TFM compatibility check. Real MSBuild uses NuGet's TFM graph; we only need
    // to satisfy SDK condition expressions like
    //   $([MSBuild]::IsTargetFrameworkCompatible('net11.0', 'net6.0')) → "true"
    // Approximation: parse "netN.M" / "netcoreappN.M" / "netstandardN.M" version numbers
    // and compare. Identifiers are matched loosely (netN+ is "compatible" with netcoreappM if
    // same major-version-or-greater). Good enough for the SDK targets that probe this.
    public static bool IsCompatible(string candidate, string requirement) {
        (string id, Version v) Parse(string s) {
            s = (s ?? "").Trim();
            int i = 0;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            var id = s.Substring(0, i).ToLowerInvariant();
            var rest = s.Substring(i);
            if (!Version.TryParse(rest.Contains('.') ? rest : rest + ".0", out var v)) v = new Version(0, 0);
            return (id, v);
        }
        var c = Parse(candidate);
        var r = Parse(requirement);
        if (c.id == r.id) return c.v >= r.v;
        // net5+ supersedes netcoreapp and (partly) netstandard.
        if (c.id == "net" && c.v.Major >= 5 && (r.id == "netcoreapp" || r.id == "netstandard"))
            return true;
        if (c.id == "netcoreapp" && r.id == "netstandard") return c.v.Major >= 1;
        return false;
    }
}

static class TargetIncrementality {
    public static bool IsUpToDate(string inputsExpanded, string outputsExpanded) {
        var inputs = ToFileList(inputsExpanded);
        var outputs = ToFileList(outputsExpanded);
        if (outputs.Count == 0) return false;
        if (inputs.Count == 0) return false;
        DateTime newestInput = DateTime.MinValue;
        foreach (var i in inputs) { var t = File.GetLastWriteTimeUtc(i); if (t > newestInput) newestInput = t; }
        DateTime oldestOutput = DateTime.MaxValue;
        foreach (var o in outputs) { var t = File.GetLastWriteTimeUtc(o); if (t == default) return false; if (t < oldestOutput) oldestOutput = t; }
        return oldestOutput >= newestInput;
    }
    static List<string> ToFileList(string s) =>
        s.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
}

static class TargetRuntime {
    public static bool TryEnter(ref int state, ref TaskCompletionSource? completion, out Task? waitTask) {
        waitTask = null;
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref state, 1, 0) == 0) {
            Volatile.Write(ref completion, created);
            return true;
        }

        if (Volatile.Read(ref state) == 2)
            return false;

        var spin = new SpinWait();
        TaskCompletionSource? existing;
        while ((existing = Volatile.Read(ref completion)) is null) {
            if (Volatile.Read(ref state) == 2)
                return false;
            spin.SpinOnce();
        }

        waitTask = existing.Task;
        return false;
    }

    public static void MarkDone(ref int state, ref TaskCompletionSource? completion) {
        Volatile.Write(ref state, 2);
        Volatile.Read(ref completion)?.TrySetResult();
    }
}

static class Log {
    public enum Verbosity { Quiet = 0, Minimal = 1, Normal = 2, Detailed = 3, Diagnostic = 4 }

    public static Verbosity Level = Verbosity.Minimal;
    static readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    static long _taskTicks;
    public static double CumulativeTaskMilliseconds =>
        System.Threading.Volatile.Read(ref _taskTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public static void ResetTaskTiming() => System.Threading.Volatile.Write(ref _taskTicks, 0);

    // Force unbuffered stdout/stderr so trace lines flush immediately. With buffering on,
    // a hot loop or hung task body looks like the prior log line was the last to write,
    // when in fact later lines are sitting in the pipe buffer.
    static Log() {
        Console.Out.Flush();
        // Replace stdout with an unbuffered writer so each line is visible immediately.
        var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        var stderr = new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetError(stderr);
    }

    // Tasks are an MSBuild-normal-verbosity concern. Normal output is intentionally
    // compact: just "<TaskName> (Xms)" after the task completes.
    public readonly struct ScopedTaskLog : IDisposable {
        readonly string _name;
        readonly long _started;
        public ScopedTaskLog(string name, long started) {
            _name = name;
            _started = started;
        }
        public void Dispose() => TaskFinished(_name, _started);
    }
    public static ScopedTaskLog Task(string name) =>
        new(name, System.Diagnostics.Stopwatch.GetTimestamp());
    public static long TaskStarted(string name) {
        return System.Diagnostics.Stopwatch.GetTimestamp();
    }
    public static void TaskFinished(string name, long started) {
        var elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        System.Threading.Interlocked.Add(ref _taskTicks, elapsedTicks);
        if (Level < Verbosity.Normal) return;
        Console.WriteLine($"{Prefix()} {name} {DurationSuffix(elapsedTicks)}");
    }

    // Target lifecycle is verbose/detailed output. Normal output stays task-focused.
    public static long TargetStarted(string name) {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Level >= Verbosity.Detailed)
            Console.WriteLine($"{Prefix()} Target started:   {name}");
        return started;
    }
    public static void TargetFinished(string name, long started) {
        if (Level >= Verbosity.Detailed)
            Console.WriteLine($"{Prefix()} Target finished:  {name} {DurationSuffix(System.Diagnostics.Stopwatch.GetTimestamp() - started)}");
    }
    public static void TargetSkipped(string name, string reason) {
        if (Level >= Verbosity.Detailed)
            Console.WriteLine($"{Prefix()} Target skipped:   {name} ({reason})");
    }
    public static void TargetUpToDate(string name) {
        if (Level >= Verbosity.Detailed)
            Console.WriteLine($"{Prefix()} Target up-to-date: {name}");
    }

    // Log routes from real SDK tasks running in the task server. Errors are also recorded
    // into Targets.Errors so the final summary surfaces them; warnings/messages flow
    // straight to the console gated by verbosity.
    public static void LogMessageHigh(string text) {
        if (Level >= Verbosity.Minimal) Console.WriteLine($"{Prefix()}   {text}");
    }
    public static void LogMessageNormal(string text) {
        if (Level >= Verbosity.Normal) Console.WriteLine($"{Prefix()}   {text}");
    }
    public static void LogMessageLow(string text) {
        if (Level >= Verbosity.Detailed) Console.WriteLine($"{Prefix()}   {text}");
    }
    public static void LogWarning(string? code, string text) {
        if (Level >= Verbosity.Minimal)
            Console.WriteLine(string.IsNullOrEmpty(code) ? $"{Prefix()} warning: {text}" : $"{Prefix()} warning {code}: {text}");
    }
    public static void LogError(string? code, string text) {
        Console.Error.WriteLine(string.IsNullOrEmpty(code) ? $"{Prefix()} error: {text}" : $"{Prefix()} error {code}: {text}");
    }

    static string Prefix() => $"[{_sw.Elapsed.TotalMilliseconds,8:F2}ms]";
    static string DurationSuffix(long elapsedTicks) {
        var ms = elapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var text = $"({ms:F2}ms)";
        if (!UseColor || ms < 2.0) return text;
        var color = ms <= 10.0 ? "\u001b[33m" : "\u001b[31m";
        return color + text + "\u001b[0m";
    }
    static bool UseColor =>
        Environment.GetEnvironmentVariable("NO_COLOR") == null
        && (Environment.GetEnvironmentVariable("BSHARP_COLOR") == "1" || !Console.IsOutputRedirected);

    public static Verbosity Parse(string s) => (s ?? "").ToLowerInvariant() switch {
        "q" or "quiet"      => Verbosity.Quiet,
        "m" or "minimal"    => Verbosity.Minimal,
        "n" or "normal"     => Verbosity.Normal,
        "d" or "detailed"   => Verbosity.Detailed,
        "diag" or "diagnostic" => Verbosity.Diagnostic,
        _                   => Verbosity.Minimal,
    };
}
""");
        sb.AppendLine();
    }

    static void EmitTaskHostRegistry(StringBuilder sb) {
        sb.AppendLine("static partial class TaskRunner {");
        sb.AppendLine("    static partial void RegisterTasks() {");
        if (_taskMeta != null) {
            foreach (var t in _taskMeta.ByTaskName.Values.OrderBy(t => t.FullTypeName, StringComparer.Ordinal)) {
                var shortName = t.FullTypeName.Contains('.')
                    ? t.FullTypeName.Substring(t.FullTypeName.LastIndexOf('.') + 1)
                    : t.FullTypeName;
                var outputs = t.OutputProperties.Count == 0
                    ? "Array.Empty<string>()"
                    : $"new[] {{ {string.Join(", ", t.OutputProperties.Select(p => CSharpLiteral(p.Name)))} }}";
                sb.AppendLine($"        Add({CSharpLiteral(shortName)}, {CSharpLiteral(t.FullTypeName)}, {CSharpLiteral(t.AssemblyPath)}, {outputs});");
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    // Emit static class P (properties). Field initializers are baked from
    // project.GetPropertyValue at codegen time so runtime startup needs no
    // conditional defaults pass. Properties can still be mutated normally.
    //
    // We emit every property we collected. Earlier versions tried to drop fields
    // with no apparent reads, but that's an unsound assumption: code (especially
    // hand-rolled tasks that do dynamic property lookups) might still want them.
    // Trust the C# / NativeAOT compiler to trim what it can.
    static void EmitP(StringBuilder sb, Microsoft.Build.Evaluation.Project project) {
        sb.AppendLine("static class P {");
        foreach (var kv in _props.OrderBy(p => p.Value, StringComparer.Ordinal)) {
            var val = NormalizeBakedPathSeparators(project.GetPropertyValue(kv.Key) ?? "");
            // Don't bake huge values (rare; multi-line aggregated lists). Fall back to "".
            if (val.Length > 4096) val = "";
            // Don't bake MSBuild* paths — they vary by csproj location and are set at runtime.
            if (kv.Key.StartsWith("msbuild", StringComparison.Ordinal)) val = "";
            // Don't bake ProjectDir for the same reason.
            if (kv.Key == "projectdir") val = "";
            var literal = val.Length == 0 ? "\"\"" : CSharpLiteral(val);
            sb.AppendLine($"    public static string {kv.Value} = {literal};");
        }
        sb.AppendLine();
        // Extras dict for property names not in registry (rare; from csproj XML for unknown names)
        sb.AppendLine("    public static Dictionary<string, string> Extras = new(StringComparer.Ordinal);");
        sb.AppendLine("    public static string GetExtra(string keyLower) => Extras.TryGetValue(keyLower, out var v) ? v : \"\";");
        sb.AppendLine("    public static void SetExtra(string keyLower, string value) => Extras[keyLower] = value ?? \"\";");
        sb.AppendLine();
        sb.AppendLine("    public static string Get(string name) {");
        sb.AppendLine("        switch (name.ToLowerInvariant()) {");
        foreach (var kv in _props.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.AppendLine($"            case \"{kv.Key}\": return {kv.Value};");
        sb.AppendLine("            default: return Extras.TryGetValue(name.ToLowerInvariant(), out var v) ? v : \"\";");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        // Set(string, value) is used for runtime csproj XML PropertyGroup reads (property names
        // come from the XML at runtime, so we can't always resolve to a typed field at codegen).
        sb.AppendLine("    public static void Set(string name, string value) {");
        sb.AppendLine("        switch (name.ToLowerInvariant()) {");
        foreach (var kv in _props.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.AppendLine($"            case \"{kv.Key}\": {kv.Value} = value ?? \"\"; break;");
        sb.AppendLine("            default: Extras[name.ToLowerInvariant()] = value ?? \"\"; break;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    // Emit static class I (items). Lists are baked from project.AllEvaluatedItems
    // as collection-expression literals so the data is essentially in the readonly
    // data segment after AOT — no runtime construction pass needed.
    //
    // Glob-sourced items (Include="**/*.cs" etc.) are NOT baked: their evaluated
    // file list is a snapshot of the codegen-time filesystem, which goes stale the
    // moment the user adds/deletes a source file. Glob items are populated at
    // runtime by InitialState.Populate's Directory.EnumerateFiles walk instead.
    static void EmitI(StringBuilder sb, Microsoft.Build.Evaluation.Project project) {
        static bool LooksLikeGlob(string s) =>
            s.Contains('*') || s.Contains('?') ||
            s.Contains("**", StringComparison.Ordinal);
        static bool IsFromGlob(Microsoft.Build.Evaluation.ProjectItem it) {
            // UnevaluatedInclude is "**/*.cs" for the original glob; ChildSourceItems is empty
            // for non-glob items and lists the underlying glob items for materialized files.
            if (LooksLikeGlob(it.UnevaluatedInclude)) return true;
            // Materialized file from a parent glob: same UnevaluatedInclude as the source.
            // Check by walking the originating item element's raw Include.
            var raw = it.Xml?.Include;
            if (raw != null && LooksLikeGlob(raw)) return true;
            return false;
        }
        static bool IsBuildArtifactPath(string include) {
            var path = include.Replace('\\', '/');
            return path.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || path.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || path.Equals(".bsharp", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(".bsharp/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/.bsharp/", StringComparison.OrdinalIgnoreCase);
        }
        static bool IsSourceLikeItemType(string itemType) =>
            itemType.Equals("Compile", StringComparison.OrdinalIgnoreCase)
            || itemType.Equals("EmbeddedResource", StringComparison.OrdinalIgnoreCase)
            || itemType.Equals("Content", StringComparison.OrdinalIgnoreCase)
            || itemType.Equals("None", StringComparison.OrdinalIgnoreCase)
            || itemType.Equals("AdditionalFiles", StringComparison.OrdinalIgnoreCase);

        var bakeMauiEvaluatedGlobs = ShouldBakeMauiEvaluatedGlobs(project);

        // Group evaluated items by lowercased item type. Skip:
        //  - source-like paths under obj/, bin/, and .bsharp/ (intermediate artifacts; would self-reference our outputs)
        //  - any item materialized from a glob (stale snapshot risk; see comment above)
        var itemsByType = project.AllEvaluatedItems
            .Where(i => !(IsSourceLikeItemType(i.ItemType) && IsBuildArtifactPath(i.EvaluatedInclude)))
            .Where(i => !IsFromGlob(i) || ShouldKeepEvaluatedGlobItem(i.ItemType, bakeMauiEvaluatedGlobs))
            .GroupBy(i => i.ItemType.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        sb.AppendLine("static class I {");
        sb.AppendLine("    static List<Item> GetOrCreateEmpty(ref List<Item>? field) {");
        sb.AppendLine("        var existing = Volatile.Read(ref field);");
        sb.AppendLine("        if (existing != null) return existing;");
        sb.AppendLine("        var created = new List<Item>();");
        sb.AppendLine("        return Interlocked.CompareExchange(ref field, created, null) ?? created;");
        sb.AppendLine("    }");
        sb.AppendLine();
        foreach (var kv in _items.OrderBy(p => p.Value, StringComparer.Ordinal)) {
            var backing = kv.Value + "Lazy";
            if (!itemsByType.TryGetValue(kv.Key, out var items) || items.Count == 0) {
                sb.AppendLine($"    static List<Item>? {backing};");
                sb.AppendLine($"    public static List<Item> {kv.Value} => GetOrCreateEmpty(ref {backing});");
                continue;
            }
            sb.AppendLine($"    public static List<Item> {kv.Value} = [");
            // Deduplicate by (Identity + sorted metadata). MSBuild items don't dedupe by
            // default — SDKs commonly add the same item via overlapping TFM-conditional
            // ItemGroups (e.g. _KnownRuntimeIdentiferPlatforms gets 38 copies of the same
            // 6 entries when targeting net11.0). For OUR purposes, identical items mean
            // duplicate work in any foreach over the list. Keep the first occurrence.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items) {
                var key = item.EvaluatedInclude + "|" + string.Join("\0",
                    item.Metadata.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                                 .Select(m => m.Name.ToLowerInvariant() + "=" + (m.EvaluatedValue ?? "")));
                if (!seen.Add(key)) continue;
                var include = CSharpLiteral(NormalizeBakedPathSeparators(item.EvaluatedInclude));
                if (item.Metadata.Any()) {
                    var metaParts = new List<string>();
                    foreach (var m in item.Metadata)
                        metaParts.Add($"[{CSharpLiteral(m.Name.ToLowerInvariant())}] = {CSharpLiteral(NormalizeBakedPathSeparators(m.EvaluatedValue ?? ""))}");
                    sb.AppendLine($"        new Item({include}, new() {{ {string.Join(", ", metaParts)} }}),");
                } else {
                    sb.AppendLine($"        new Item({include}),");
                }
            }
            sb.AppendLine("    ];");
        }
        sb.AppendLine();
        sb.AppendLine("    public static Dictionary<string, List<Item>> Extras = new(StringComparer.Ordinal);");
        sb.AppendLine();
        sb.AppendLine("    public static List<Item> Get(string type) {");
        sb.AppendLine("        switch (type.ToLowerInvariant()) {");
        foreach (var kv in _items.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.AppendLine($"            case \"{kv.Key}\": return {kv.Value};");
        sb.AppendLine("            default:");
        sb.AppendLine("                var k = type.ToLowerInvariant();");
        sb.AppendLine("                if (!Extras.TryGetValue(k, out var list)) { list = new(); Extras[k] = list; }");
        sb.AppendLine("                return list;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    static string NormalizeBakedPathSeparators(string value) {
        if (Path.DirectorySeparatorChar == '\\' || string.IsNullOrEmpty(value) || !value.Contains('\\'))
            return value;
        return value.Replace('\\', Path.DirectorySeparatorChar);
    }

    static bool ShouldBakeMauiEvaluatedGlobs(Microsoft.Build.Evaluation.Project project) {
        // MAUI single-project builds use SDK-evaluated glob includes/excludes to keep
        // platform sources and XAML/CSS metadata in sync for each inner build. The generic
        // runtime file walks are too broad and don't know MAUI item metadata.
        return project.GetPropertyValue("SingleProject").Equals("true", StringComparison.OrdinalIgnoreCase)
            || project.GetPropertyValue("UseMaui").Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    static bool ShouldKeepEvaluatedGlobItem(string itemType, bool bakeMauiEvaluatedGlobs) =>
        bakeMauiEvaluatedGlobs
        && (itemType.Equals("Compile", StringComparison.OrdinalIgnoreCase)
            || itemType.Equals("MauiXaml", StringComparison.OrdinalIgnoreCase)
            || itemType.Equals("MauiCss", StringComparison.OrdinalIgnoreCase));

    static void EmitTasks(StringBuilder sb) {
        sb.AppendLine("""
readonly struct ParamList {
    public static readonly ParamList Empty = default;
    readonly (string Key, string Value)[]? _items;
    readonly string? _key;
    readonly string? _value;
    public ParamList((string Key, string Value)[] items) {
        _items = items;
        _key = null;
        _value = null;
    }
    public ParamList(string key, string value) {
        _items = null;
        _key = key;
        _value = value;
    }
    public string? GetValueOrDefault(string key) {
        if (_key != null) {
            if (string.Equals(_key, key, StringComparison.OrdinalIgnoreCase))
                return _value;
        } else if (_items != null) {
            foreach (var (k, v) in _items)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    return v;
        }
        return null;
    }
}

readonly struct OutputList {
    readonly (string Key, string itemName, string? metadataName)[]? _items;
    readonly string? _key;
    readonly string? _itemName;
    readonly string? _metadataName;
    public OutputList((string Key, string itemName, string? metadataName)[] items) {
        _items = items;
        _key = null;
        _itemName = null;
        _metadataName = null;
    }
    public OutputList(string key, string itemName, string? metadataName) {
        _items = null;
        _key = key;
        _itemName = itemName;
        _metadataName = metadataName;
    }
    public bool TryGetValue(string key, out (string itemName, string? metadataName) value) {
        if (_key != null) {
            if (string.Equals(_key, key, StringComparison.OrdinalIgnoreCase)) {
                value = (_itemName!, _metadataName);
                return true;
            }
        } else if (_items != null) {
            foreach (var item in _items) {
                if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) {
                    value = (item.itemName, item.metadataName);
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}

static class OutputListExtensions {
    public static bool TryGetValue(this OutputList? outputs, string key, out (string itemName, string? metadataName) value) {
        if (outputs.HasValue)
            return outputs.Value.TryGetValue(key, out value);
        value = default;
        return false;
    }
}

readonly struct SplitList {
    readonly string _value;
    readonly char _separator;
    readonly bool _removeEmpty;
    public SplitList(string? value, char separator = ';', bool removeEmpty = true) {
        _value = value ?? "";
        _separator = separator;
        _removeEmpty = removeEmpty;
    }
    public Enumerator GetEnumerator() => new(_value, _separator, _removeEmpty);

    public struct Enumerator {
        readonly string _value;
        readonly char _separator;
        readonly bool _removeEmpty;
        int _next;
        public string Current { get; private set; }
        public Enumerator(string value, char separator, bool removeEmpty) {
            _value = value;
            _separator = separator;
            _removeEmpty = removeEmpty;
            _next = 0;
            Current = "";
        }
        public bool MoveNext() {
            while (_next <= _value.Length) {
                var start = _next;
                var end = _value.IndexOf(_separator, start);
                if (end < 0) {
                    end = _value.Length;
                    _next = _value.Length + 1;
                } else {
                    _next = end + 1;
                }
                if (_removeEmpty && end == start) continue;
                Current = start == 0 && end == _value.Length ? _value : _value.Substring(start, end - start);
                return true;
            }
            return false;
        }
    }
}

readonly ref struct SemicolonSplit {
    readonly ReadOnlySpan<char> _value;
    public SemicolonSplit(string? value) => _value = value.AsSpan();
    public Enumerator GetEnumerator() => new(_value);

    public ref struct Enumerator {
        ReadOnlySpan<char> _remaining;
        public ReadOnlySpan<char> Current { get; private set; }
        public Enumerator(ReadOnlySpan<char> value) {
            _remaining = value;
            Current = default;
        }
        public bool MoveNext() {
            while (true) {
                if (_remaining.IsEmpty) return false;
                var index = _remaining.IndexOf(';');
                ReadOnlySpan<char> segment;
                if (index < 0) {
                    segment = _remaining;
                    _remaining = default;
                } else {
                    segment = _remaining[..index];
                    _remaining = _remaining[(index + 1)..];
                }
                segment = segment.Trim();
                if (segment.IsEmpty) continue;
                Current = segment;
                return true;
            }
        }
    }
}

static partial class Tasks {
    public static void Message(ParamList p) { }
    public static void MakeDir(ParamList p, OutputList? outputs) {
        var created = new List<Item>();
        foreach (var d in new SplitList(p.GetValueOrDefault("Directories")))
            if (!Directory.Exists(d)) { Directory.CreateDirectory(d); created.Add(new Item(d)); }
        if (outputs != null && outputs.TryGetValue("DirectoriesCreated", out var spec))
            I.Get(spec.itemName).AddRange(created);
    }
    public static void WriteLinesToFile(ParamList p) {
        var file = p.GetValueOrDefault("File") ?? "";
        var lines = p.GetValueOrDefault("Lines") ?? "";
        var overwrite = (p.GetValueOrDefault("Overwrite") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
        // Items in the "Lines" string are joined with `;` by the codegen. That conflicts with
        // any line that itself contains `;` (e.g. C# `global using System;`). MSBuild's
        // standard escape replaces `;` inside item values with `%3B` before joining; we
        // honor that here so split-and-unescape is safe.
        var ls = lines.Split(';', StringSplitOptions.None)
            .Select(s => s.Replace("%3B", ";").Replace("%3b", ";"))
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (overwrite) {
            if (File.Exists(file) && File.ReadLines(file).SequenceEqual(ls))
                return;
            File.WriteAllLines(file, ls);
        }
        else File.AppendAllLines(file, ls);
    }
    public static void Touch(ParamList p, OutputList? outputs) {
        var alwaysCreate = (p.GetValueOrDefault("AlwaysCreate") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
        var time = DateTime.UtcNow;
        var touched = new List<Item>();
        foreach (var f in new SplitList(p.GetValueOrDefault("Files"))) {
            if (!File.Exists(f)) { if (!alwaysCreate) continue; Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, ""); }
            File.SetLastWriteTimeUtc(f, time);
            touched.Add(new Item(f));
        }
        if (outputs != null && outputs.TryGetValue("TouchedFiles", out var spec)) I.Get(spec.itemName).AddRange(touched);
    }
    public static void Delete(ParamList p, OutputList? outputs) {
        var deleted = new List<Item>();
        foreach (var f in new SplitList(p.GetValueOrDefault("Files"))) if (File.Exists(f)) { File.Delete(f); deleted.Add(new Item(f)); }
        if (outputs != null && outputs.TryGetValue("DeletedFiles", out var spec)) I.Get(spec.itemName).AddRange(deleted);
    }
    public static void Copy(ParamList p, OutputList? outputs) {
        var src = StringList.FromSemicolonList(p.GetValueOrDefault("SourceFiles") ?? "");
        var dst = StringList.FromSemicolonList(p.GetValueOrDefault("DestinationFiles") ?? "");
        var skipUnchanged = (p.GetValueOrDefault("SkipUnchangedFiles") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
        var copied = new List<Item>();
        for (int i = 0; i < src.Length; i++) {
            if (i >= dst.Length) break;
            var s1 = src[i]; var d1 = dst[i];
            if (skipUnchanged && File.Exists(d1) && File.GetLastWriteTimeUtc(s1) <= File.GetLastWriteTimeUtc(d1)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(d1)!);
            File.Copy(s1, d1, true);
            copied.Add(new Item(d1));
        }
        if (outputs != null && outputs.TryGetValue("CopiedFiles", out var spec)) I.Get(spec.itemName).AddRange(copied);
    }
    public static void Error(ParamList p) {
        throw new InvalidOperationException($"<Error>: {p.GetValueOrDefault("Text") ?? ""}");
    }
    public static void Warning(ParamList p) { }
    public static void ConvertToAbsolutePath(ParamList p, OutputList? outputs) {
        var abs = new List<Item>();
        foreach (var path in new SplitList(p.GetValueOrDefault("Paths")))
            abs.Add(new Item(Path.GetFullPath(path)));
        if (outputs != null && outputs.TryGetValue("AbsolutePaths", out var spec)) I.Get(spec.itemName).AddRange(abs);
    }
    public static void RemoveDir(ParamList p) {
        foreach (var d in new SplitList(p.GetValueOrDefault("Directories"))) if (Directory.Exists(d)) Directory.Delete(d, true);
    }
    public static void CreateProperty(ParamList p, OutputList? outputs) {
        var value = p.GetValueOrDefault("Value") ?? "";
        if (outputs != null && outputs.TryGetValue("Value", out var spec) && spec.itemName != null) P.Set(spec.itemName, value);
    }
    public static void CreateItem(ParamList p, OutputList? outputs) {
        var items = new List<Item>();
        foreach (var include in new SplitList(p.GetValueOrDefault("Include")))
            items.Add(new Item(include));
        if (outputs != null && outputs.TryGetValue("Include", out var spec)) I.Get(spec.itemName).AddRange(items);
    }
    public static void FindUnderPath(ParamList p, OutputList? outputs) { }
    public static void ReadLinesFromFile(ParamList p, OutputList? outputs) {
        var file = p.GetValueOrDefault("File") ?? "";
        if (!File.Exists(file)) return;
        var lines = File.ReadAllLines(file);
        if (outputs != null && outputs.TryGetValue("Lines", out var spec))
            I.Get(spec.itemName).AddRange(lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => new Item(l)));
    }

    // Generic external-process task. Shells out via ProcessStartInfo.
    public static void Exec(ParamList p, OutputList? outputs) {
        var command = p.GetValueOrDefault("Command") ?? "";
        if (string.IsNullOrWhiteSpace(command)) return;
        var workingDir = p.GetValueOrDefault("WorkingDirectory");
        var ignoreExitCode = (p.GetValueOrDefault("IgnoreExitCode") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
        var consoleToMsbuild = (p.GetValueOrDefault("ConsoleToMSBuild") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);

        // Pick a shell. MSBuild on Unix uses /bin/sh -c.
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows()) {
            psi = new ProcessStartInfo("cmd.exe");
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        } else {
            psi = new ProcessStartInfo("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        psi.UseShellExecute = false;
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        if (consoleToMsbuild) { psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; }

        var proc = Process.Start(psi)!;
        var stdoutLines = new List<string>();
        if (consoleToMsbuild) {
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) { stdoutLines.Add(e.Data); Console.WriteLine(e.Data); } };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
            proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
        }
        proc.WaitForExit();
        if (outputs != null) {
            if (outputs.TryGetValue("ExitCode", out var ec) && ec.itemName != null) P.Set(ec.itemName, proc.ExitCode.ToString());
            if (consoleToMsbuild && outputs.TryGetValue("ConsoleOutput", out var co))
                I.Get(co.itemName).AddRange(stdoutLines.Select(l => new Item(l)));
        }
        if (proc.ExitCode != 0 && !ignoreExitCode)
            throw new InvalidOperationException($"<Exec> failed ({proc.ExitCode}): {command}");
    }

    // Hash: XxHash64 over the items list (joined with NUL). Used for incrementality
    // fingerprints — non-cryptographic is fine here, the hash only needs to be stable
    // across invocations of the same generated binary.
    public static void Hash(ParamList p, OutputList? outputs) {
        var ignoreCase = (p.GetValueOrDefault("IgnoreCase") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
        var hasher = new System.IO.Hashing.XxHash64();
        Span<byte> nul = stackalloc byte[1];
        foreach (var it in new SplitList(p.GetValueOrDefault("ItemsToHash"))) {
            var s = ignoreCase ? it.ToLowerInvariant() : it;
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(s);
            if (byteCount <= 512) {
                Span<byte> bytes = stackalloc byte[byteCount];
                System.Text.Encoding.UTF8.GetBytes(s, bytes);
                hasher.Append(bytes);
            } else {
                var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
                try {
                    var written = System.Text.Encoding.UTF8.GetBytes(s, rented);
                    hasher.Append(rented.AsSpan(0, written));
                } finally {
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                }
            }
            hasher.Append(nul);
        }
        var hex = Convert.ToHexString(hasher.GetCurrentHash());
        if (outputs != null && outputs.TryGetValue("HashResult", out var spec) && spec.metadataName == "property")
            P.Set(spec.itemName, hex);
    }

    public static void MSBuildInternalMessage(ParamList p) { }

    public static void NetSdkWarning(ParamList p) { }

    public static void MSBuild(ParamList p) {
        var projects = StringList.FromSemicolonList(p.GetValueOrDefault("Projects") ?? "");
        if (projects.Length == 0) return;
        var targets = StringList.FromSemicolonList(p.GetValueOrDefault("Targets") ?? "");
        if (targets.Length == 0) return;

        var currentProject = P.MSBuildProjectFullPath;
        bool selfOnly = projects.All(project =>
            string.Equals(Path.GetFullPath(project), currentProject, StringComparison.OrdinalIgnoreCase));
        if (!selfOnly)
            throw new InvalidOperationException("<MSBuild> cross-project recursion is not supported in v1");

        foreach (var target in targets)
            Targets.Run(target.Trim()).GetAwaiter().GetResult();
    }

    public static void SetRidAgnosticValueForProjects(ParamList p) { }

    public static void NotImplemented(string taskName) =>
        throw new InvalidOperationException($"task not implemented in v1: <{taskName} ... />");
}
""");
        sb.AppendLine();
    }

    static void EmitInitialState(StringBuilder sb, Microsoft.Build.Evaluation.Project project) {
        sb.AppendLine("static class InitialState {");
        sb.AppendLine("    public static void Populate(string csprojPath) {");
        sb.AppendLine("        P.Set(\"MSBuildProjectFullPath\", csprojPath);");
        sb.AppendLine("        P.Set(\"MSBuildProjectFile\", Path.GetFileName(csprojPath));");
        sb.AppendLine("        P.Set(\"MSBuildProjectName\", Path.GetFileNameWithoutExtension(csprojPath));");
        sb.AppendLine("        P.Set(\"MSBuildProjectExtension\", Path.GetExtension(csprojPath));");
        sb.AppendLine("        var projectDir = Path.GetDirectoryName(csprojPath)!;");
        sb.AppendLine("        P.Set(\"MSBuildProjectDirectory\", projectDir);");
        sb.AppendLine("        P.Set(\"ProjectDir\", projectDir + Path.DirectorySeparatorChar);");
        sb.AppendLine("        P.Set(\"MSBuildThisFile\", Path.GetFileName(csprojPath));");
        sb.AppendLine("        P.Set(\"MSBuildThisFileFullPath\", csprojPath);");
        sb.AppendLine("        P.Set(\"MSBuildThisFileDirectory\", projectDir + Path.DirectorySeparatorChar);");
        sb.AppendLine();
        sb.AppendLine("        // Task host artifacts live next to the real published binary. Resolve .bsharp/build symlinks.");
        sb.AppendLine("        var executable = System.Environment.ProcessPath ?? typeof(Targets).Assembly.Location;");
        sb.AppendLine("        var executableInfo = new FileInfo(executable);");
        sb.AppendLine("        if (!string.IsNullOrEmpty(executableInfo.LinkTarget)) executable = Path.GetFullPath(executableInfo.LinkTarget, Path.GetDirectoryName(executable)!);");
        sb.AppendLine("        TaskRunner.Init(Path.Combine(Path.GetDirectoryName(executable)!, \"tasks\"));");
        sb.AppendLine();
        sb.AppendLine("        // User-set properties from csproj XML (override the baked field defaults).");
        sb.AppendLine("        PopulateProjectProperties(csprojPath);");
        sb.AppendLine();
        sb.AppendLine("        // SDK property defaults and initial items are baked as static field");
        sb.AppendLine("        // initializers in P and I (see those classes). No runtime population needed.");
        sb.AppendLine();
        if (!ShouldBakeMauiEvaluatedGlobs(project)) {
            sb.AppendLine("        // Default Compile glob (EnableDefaultCompileItems).");
            // Use typed field if available — the only place we'd have used P.Get(string).
            string enableCompileExpr = TryCanonicalProp("enabledefaultcompileitems", out var enableCanon)
                ? $"P.{enableCanon}"
                : "P.GetExtra(\"enabledefaultcompileitems\")";
            sb.AppendLine($"        if ({enableCompileExpr}.Equals(\"true\", StringComparison.OrdinalIgnoreCase)");
            sb.AppendLine($"            || string.IsNullOrEmpty({enableCompileExpr})) {{");
            if (TryCanonicalItem("compile", out var compileCanon)) {
                sb.AppendLine($"            var existing = new HashSet<string>(I.{compileCanon}.Select(c => c.Identity), StringComparer.OrdinalIgnoreCase);");
                sb.AppendLine("            foreach (var f in FastPathFileHelpers.EnumerateProjectSourceFiles(projectDir)) {");
                sb.AppendLine($"                if (existing.Add(f)) I.{compileCanon}.Add(new Item(f));");
                sb.AppendLine("            }");
            }
            sb.AppendLine("        }");
        }
        // -p:X=Y global properties — unconditional final override after csproj + SDK defaults.
        // Matches MSBuild semantics: globals always win over csproj <PropertyGroup> values.
        var globals = project.GlobalProperties;
        if (globals.Count > 0) {
            sb.AppendLine();
            sb.AppendLine("        // Global properties from -p:X=Y (unconditional; overrides csproj + SDK defaults).");
            foreach (var kv in globals.OrderBy(g => g.Key, StringComparer.Ordinal)) {
                var lower = kv.Key.ToLowerInvariant();
                if (lower.StartsWith("msbuild")) continue;
                sb.AppendLine($"        P.Set({CSharpLiteral(kv.Key)}, {CSharpLiteral(kv.Value)});");
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("""
    static void PopulateProjectProperties(string csprojPath) {
        var settings = new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
        };
        using var reader = XmlReader.Create(csprojPath, settings);
        while (reader.Read()) {
            if (reader.NodeType != XmlNodeType.Element || !reader.LocalName.Equals("PropertyGroup", StringComparison.Ordinal))
                continue;

            var propertyGroupDepth = reader.Depth;
            if (reader.IsEmptyElement)
                continue;

            while (reader.Read()) {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == propertyGroupDepth)
                    break;

                if (reader.NodeType != XmlNodeType.Element || reader.Depth != propertyGroupDepth + 1)
                    continue;

                var name = reader.LocalName;
                var value = reader.IsEmptyElement ? "" : reader.ReadElementContentAsString();
                P.Set(name, value);
            }
        }
    }
""");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    static void EmitTargets(StringBuilder sb, ProjectInstance instance, List<string> sequence, string entryTarget) {
        // 1. Build name → method-index map (1-based, matches old codegen).
        var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sequence.Count; i++) indexOf[sequence[i]] = i + 1;
        string Method(string name) => $"T_{indexOf[name]:D3}_{SanitizeIdent(name)}";

        // 2. Collect properties mutated inside any target body, considering:
        //    (a) <PropertyGroup> elements directly inside the target
        //    (b) <Output PropertyName="..."/> on task invocations
        //    Properties NOT in this set are safe to expand statically: their final value
        //    after top-level evaluation never changes during target execution.
        var mutatedInTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in instance.Targets.Values) {
            foreach (var child in target.Children) {
                switch (child) {
                    case ProjectPropertyGroupTaskInstance pg:
                        foreach (var prop in pg.Properties) mutatedInTargets.Add(prop.Name);
                        break;
                    case ProjectTaskInstance task:
                        foreach (var output in task.Outputs)
                            if (output is ProjectTaskOutputPropertyInstance op)
                                mutatedInTargets.Add(op.PropertyName);
                        break;
                }
            }
        }

        // 3. Parse a DependsOnTargets/Before/AfterTargets string into segments classified as:
        //    - "literal":  bare target name present in the emitted set → direct call
        //    - "expr":     unresolved dynamic expression → foreach + Run() fallback
        //    Static expansion: when a segment is `$(X)` and X is never mutated inside a target,
        //    we substitute `instance.GetPropertyValue(X)` (the fully-evaluated value) and
        //    classify each split piece. Unknown literal names are dropped silently
        //    (matches MSBuild's behavior for missing DependsOnTargets entries).
        bool anyDynamicEmitted = false;

        List<(string kind, string val)> ParseDepString(string? raw) {
            var result = new List<(string, string)>();
            if (string.IsNullOrEmpty(raw)) return result;
            foreach (var seg in SplitTopLevelSegments(raw)) {
                var t = seg.Trim();
                if (t.Length == 0) continue;
                bool dyn = t.IndexOfAny(new[] { '$', '@', '%' }) >= 0;
                if (!dyn) {
                    if (indexOf.ContainsKey(t)) result.Add(("literal", t));
                    continue;
                }
                if (IsPureSinglePropRef(t, out var propName) && !mutatedInTargets.Contains(propName)) {
                    var expanded = instance.GetPropertyValue(propName);
                    foreach (var inner in SplitTopLevelSegments(expanded)) {
                        var it = inner.Trim();
                        if (it.Length == 0) continue;
                        bool innerDyn = it.IndexOfAny(new[] { '$', '@', '%' }) >= 0;
                        if (innerDyn) result.Add(("expr", it));
                        else if (indexOf.ContainsKey(it)) result.Add(("literal", it));
                        // else: unknown literal → drop
                    }
                    continue;
                }
                result.Add(("expr", t));
            }
            return result;
        }

        // 4. Reverse-index for Before/AfterTargets: target name → ordered list of companions.
        //    Use ParseDepString so we benefit from static expansion for `BeforeTargets="$(X)"`.
        var beforeCompanions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var afterCompanions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in sequence) {
            if (!instance.Targets.TryGetValue(name, out var t)) continue;
            foreach (var (kind, val) in ParseDepString(t.BeforeTargets))
                if (kind == "literal") {
                    if (!beforeCompanions.TryGetValue(val, out var list)) { list = new(); beforeCompanions[val] = list; }
                    list.Add(name);
                }
            foreach (var (kind, val) in ParseDepString(t.AfterTargets))
                if (kind == "literal") {
                    if (!afterCompanions.TryGetValue(val, out var list)) { list = new(); afterCompanions[val] = list; }
                    list.Add(name);
                }
        }

        // 5. Emit target methods into a buffer first so we can decide whether the
        //    Run(string) dispatcher is needed.
        var bodies = new StringBuilder();
        foreach (var name in sequence) {
            var method = Method(name);

            if (!instance.Targets.TryGetValue(name, out var target)) {
                bodies.AppendLine($"    public static ValueTask {method}() => ValueTask.CompletedTask; // Target '{name}' missing from evaluated project");
                bodies.AppendLine();
                continue;
            }

            if (IsTrivialNoOpTarget(name, target, beforeCompanions, afterCompanions, ParseDepString)) {
                bodies.AppendLine($"    public static ValueTask {method}() => ValueTask.CompletedTask;");
                bodies.AppendLine();
                continue;
            }

            bodies.AppendLine($"    static int {method}State;");
            bodies.AppendLine($"    static TaskCompletionSource? {method}Completion;");
            bodies.AppendLine($"    public static async ValueTask {method}() {{");
            bodies.AppendLine($"        if (!TargetRuntime.TryEnter(ref {method}State, ref {method}Completion, out var waitTask)) {{");
            bodies.AppendLine("            if (waitTask is not null)");
            bodies.AppendLine("                await waitTask;");
            bodies.AppendLine("            return;");
            bodies.AppendLine("        }");
            bodies.AppendLine("        try {");

            // Write the body to a scratch buffer; if EmitTargetMethodBody throws mid-emission
            // (e.g., uncompilable Condition or body expression), discard the partial output and
            // emit a silent no-op with an explanatory comment. The previous behavior of
            // Errors.Add(...) here caused FALSE-POSITIVE errors: every invocation of the target
            // would report an error even when MSBuild would have silently skipped it because
            // the Condition was false. Silent skip matches the common case where the affected
            // target is an SDK `_Check*` target that fires `<Error>` only when its Condition is
            // true. Real targets that should always run will surface failure at a downstream
            // missing-output if we silently drop their body — accept that trade-off.
            var body = new StringBuilder();
            try {
                EmitTargetMethodBody(
                    body, name, target, beforeCompanions, afterCompanions,
                    ParseDepString, Method,
                    onDynamicEmitted: () => anyDynamicEmitted = true,
                    instance: instance);
                bodies.Append(body);
            } catch (Exception ex) {
                bodies.AppendLine($"            // codegen failed: {CSharpEscape(ex.Message)}");
                bodies.AppendLine("            // (target replaced with no-op; see comment above)");
            }
            bodies.AppendLine("        } finally {");
            bodies.AppendLine($"            TargetRuntime.MarkDone(ref {method}State, ref {method}Completion);");
            bodies.AppendLine("        }");
            bodies.AppendLine("    }");
            bodies.AppendLine();
        }

        // 6. Emit the Targets class.
        sb.AppendLine("static class Targets {");
        sb.AppendLine("    public static readonly List<(string target, string error)> Errors = new();");
        sb.AppendLine("    static void AddError(string target, string error) { lock (Errors) Errors.Add((target, error)); }");
        sb.AppendLine();
        // Public entry point — direct call so Main doesn't root the dispatcher switch.
        sb.AppendLine($"    public static ValueTask Build() => {Method(entryTarget)}();");
        if (indexOf.TryGetValue("Restore", out _))
            sb.AppendLine($"    public static ValueTask Restore() => {Method("Restore")}();");
        else
            sb.AppendLine("    public static ValueTask Restore() => ValueTask.CompletedTask;");
        sb.AppendLine();

        if (anyDynamicEmitted || _hasCallTarget) {
            sb.AppendLine("    // Dispatcher for genuinely-dynamic deps and CallTarget. Roots all target methods.");
            sb.AppendLine("    public static async ValueTask Run(string name) {");
            sb.AppendLine("        if (string.IsNullOrEmpty(name)) return;");
            sb.AppendLine("        try {");
            sb.AppendLine("            switch (name.ToLowerInvariant()) {");
            foreach (var name in sequence) {
                sb.AppendLine($"                case \"{name.ToLowerInvariant()}\": await {Method(name)}(); break;");
            }
            sb.AppendLine("                default: break; // unknown target: silent skip (matches MSBuild)");
            sb.AppendLine("            }");
            sb.AppendLine("        } catch (Exception ex) {");
            sb.AppendLine("            AddError(name, ex.Message);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.Append(bodies);
        sb.AppendLine("}");
    }

    static bool IsTrivialNoOpTarget(
        string name,
        ProjectTargetInstance target,
        Dictionary<string, List<string>> beforeCompanions,
        Dictionary<string, List<string>> afterCompanions,
        Func<string?, List<(string kind, string val)>> parseDeps)
    {
        if (target.Children.Any()) return false;
        if (parseDeps(target.DependsOnTargets).Count != 0) return false;
        if (beforeCompanions.TryGetValue(name, out var befores) && befores.Count != 0) return false;
        if (afterCompanions.TryGetValue(name, out var afters) && afters.Count != 0) return false;
        return true;
    }

    // Returns true if `s` is exactly `$(Identifier)` with no nested refs or operators.
    // The identifier must contain only letter/digit/underscore.
    static bool IsPureSinglePropRef(string s, out string propName) {
        propName = "";
        var t = s.Trim();
        if (t.Length < 4 || !t.StartsWith("$(") || !t.EndsWith(")")) return false;
        var inner = t.Substring(2, t.Length - 3);
        if (inner.Length == 0) return false;
        foreach (var c in inner) {
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        }
        propName = inner;
        return true;
    }

    // Split a DependsOnTargets/Before/AfterTargets string on top-level ';' and newlines,
    // ignoring separators that appear inside $(...) / @(...) / %(...) expressions.
    static List<string> SplitTopLevelSegments(string s) {
        var result = new List<string>();
        if (string.IsNullOrEmpty(s)) return result;
        int depth = 0;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++) {
            char c = s[i];
            if ((c == '$' || c == '@' || c == '%') && i + 1 < s.Length && s[i + 1] == '(') {
                depth++;
                sb.Append(c); sb.Append('(');
                i++;
                continue;
            }
            if (c == ')' && depth > 0) { depth--; sb.Append(c); continue; }
            if ((c == ';' || c == '\n' || c == '\r') && depth == 0) {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    static void EmitTargetMethodBody(
        StringBuilder sb,
        string name,
        ProjectTargetInstance target,
        Dictionary<string, List<string>> beforeCompanions,
        Dictionary<string, List<string>> afterCompanions,
        Func<string?, List<(string kind, string val)>> ParseDeps,
        Func<string, string> Method,
        Action onDynamicEmitted,
        ProjectInstance instance)
    {
        // Wrap the entire method body in try/catch so:
        //  - codegen-time-error throws emitted in place of uncompilable expressions are caught
        //  - uncaught exceptions from dep calls are attributed to this target
        //  - condition/incrementality evaluation failures don't crash the build
        // Body errors still get caught here (no separate inner try/catch needed).
        sb.AppendLine("        try {");

        // Condition gate: skip entire target (deps + body + before/after companions).
        // Small deviation from MSBuild (which still runs Before/After), accepted for
        // a clean execute-once invariant.
        //
        // Two-stage compilation: try CondCompiler directly first. If it fails (e.g. the
        // condition uses property functions or instance methods we don't compile yet),
        // ask MSBuild's own evaluator to ExpandString the condition — that resolves
        // $(X), @(X), $([MSBuild]::F(...)), $(X.Method(...)) all to literal values —
        // then re-compile the much simpler expanded form. If even THAT fails we let
        // the outer catch in EmitTargets stub the target as a no-op.
        if (!string.IsNullOrEmpty(target.Condition))
            sb.AppendLine($"            if ({NegateBoolExpr(CompileCondWithFallback(target.Condition, instance))}) {{ Log.TargetSkipped({CSharpLiteral(name)}, \"condition was false\"); return; }}");

        // DependsOnTargets are ordered by MSBuild semantics. We still de-duplicate a
        // literal run because target methods are execute-once, but we must not flatten the
        // run into Task.WhenAll: later targets often consume item/property mutations from
        // earlier targets (e.g. CoreCompile needs references populated by ResolveReferences).
        var literalRun = new List<string>();
        var literalRunSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void FlushLiteralRun() {
            if (literalRun.Count == 0) return;
            foreach (var dependency in literalRun)
                sb.AppendLine($"            await {Method(dependency)}();");
            literalRun.Clear();
            literalRunSeen.Clear();
        }
        foreach (var (kind, val) in ParseDeps(target.DependsOnTargets)) {
        if (kind == "literal") {
                if (literalRunSeen.Add(val))
                    literalRun.Add(val);
            } else {
                FlushLiteralRun();
                sb.AppendLine($"            foreach (var dependencyName in ({CompileExpr(val)}).Split(';', StringSplitOptions.RemoveEmptyEntries))");
                sb.AppendLine("                await Run(dependencyName.Trim());");
                onDynamicEmitted();
            }
        }
        FlushLiteralRun();

        // Before-companions (literal X with X.BeforeTargets containing this target) run
        // after DependsOnTargets, but can run as their own static prerequisite batch.
        if (beforeCompanions.TryGetValue(name, out var befores))
            foreach (var before in befores)
                if (literalRunSeen.Add(before))
                    literalRun.Add(before);
        FlushLiteralRun();

        // Dependencies are now satisfied. Log the target start immediately before this
        // target's own body/up-to-date check so parent targets don't appear to run while
        // their prerequisites are still executing.
        sb.AppendLine($"            var targetStart = Log.TargetStarted({CSharpLiteral(name)});");

        bool hasIncr = !string.IsNullOrEmpty(target.Inputs) && !string.IsNullOrEmpty(target.Outputs);
        bool hasAfters = afterCompanions.TryGetValue(name, out var afters);

        // Body guarded by Inputs/Outputs incrementality if both are set. After-companions
        // run regardless of up-to-date skip (matches MSBuild).
        if (hasIncr) {
            sb.AppendLine($"            if (!TargetIncrementality.IsUpToDate({CompileExpr(target.Inputs)}, {CompileExpr(target.Outputs)})) {{");
        }
        foreach (var child in target.Children) EmitChild(sb, child);
        if (hasIncr) {
            sb.AppendLine("            } else {");
            sb.AppendLine($"                Log.TargetUpToDate({CSharpLiteral(name)});");
            sb.AppendLine("            }");
        }

        if (hasAfters) {
            foreach (var a in afters!) sb.AppendLine($"            await {Method(a)}();");
        }

        sb.AppendLine($"            Log.TargetFinished({CSharpLiteral(name)}, targetStart);");
        sb.AppendLine("        } catch (Exception ex) {");
        sb.AppendLine($"            AddError({CSharpLiteral(name)}, ex.Message);");
        sb.AppendLine("        }");
    }

    static void EmitChild(StringBuilder sb, ProjectTargetInstanceChild child) {
        switch (child) {
            case ProjectPropertyGroupTaskInstance pg: EmitPropertyGroup(sb, pg); break;
            case ProjectItemGroupTaskInstance ig:    EmitItemGroup(sb, ig); break;
            case ProjectTaskInstance task:           EmitTask(sb, task); break;
            case ProjectOnErrorInstance oe:          sb.AppendLine($"        // <OnError ExecuteTargets=\"{oe.ExecuteTargets}\"/>"); break;
            default:                                 sb.AppendLine($"        // unknown child: {child.GetType().Name}"); break;
        }
    }

    static void EmitPropertyGroup(StringBuilder sb, ProjectPropertyGroupTaskInstance pg) {
        var ind = "        ";
        var strs = new List<string?> { pg.Condition };
        foreach (var prop in pg.Properties) { strs.Add(prop.Condition); strs.Add(prop.Value); }
        // Same @(X)-based inference as for tasks.
        string? batch = InferBatchType(strs, itemRefScopeExprs: strs);
        if (batch != null) { sb.AppendLine($"{ind}foreach (var batchItem in {ItemAccess(batch)}) {{"); ind += "    "; }
        if (!string.IsNullOrEmpty(pg.Condition)) sb.AppendLine($"{ind}if ({CompileCond(pg.Condition, batch)}) {{");
        else sb.AppendLine($"{ind}{{");
        foreach (var prop in pg.Properties) {
            var iind = ind + "    ";
            var setExpr = SetProperty(prop.Name, CompileExpr(prop.Value, batch));
            if (!string.IsNullOrEmpty(prop.Condition))
                sb.AppendLine($"{iind}if ({CompileCond(prop.Condition, batch)}) {setExpr};");
            else
                sb.AppendLine($"{iind}{setExpr};");
        }
        sb.AppendLine($"{ind}}}");
        if (batch != null) sb.AppendLine($"        }}");
    }

    static void EmitItemGroup(StringBuilder sb, ProjectItemGroupTaskInstance ig) {
        var outer = "        ";
        // Open ItemGroup-level condition (shared across all items).
        if (!string.IsNullOrEmpty(ig.Condition)) sb.AppendLine($"{outer}if ({CompileCond(ig.Condition)}) {{");
        else sb.AppendLine($"{outer}{{");
        var ind = outer + "    ";

        var items = ig.Items.ToList();
        for (var itemIndex = 0; itemIndex < items.Count; itemIndex++) {
            var item = items[itemIndex];
            var itemCondition = item.Condition;
            if (TryMergeAdjacentItemAdditions(items, itemIndex, out var mergedCount, out var mergedCondition)) {
                itemCondition = mergedCondition;
                itemIndex += mergedCount - 1;
            }

            var iind = ind;
            // Per-item batch inference: each item's metadata refs determine its own batch.
            // ItemGroups commonly mix item types where the same scope refs %(X.Y) for one
            // item and %(Z.W) for another — per-item lets us handle these without forcing
            // a multi-batch outer foreach.
            var strs = new List<string?> { itemCondition, item.Include, item.Remove };
            foreach (var m in item.Metadata) { strs.Add(m.Condition); strs.Add(m.Value); }
            // Self-batch hint: the item type being DEFINED can serve as the implicit batch
            // context for unqualified `%(Meta)` (matches MSBuild's <X Y="%(M)" /> semantics).
            string? batch = InferBatchType(strs,
                itemRefScopeExprs: strs,
                selfBatchHint: TryCanonicalItemKey(item.ItemType));

            // Fast path for the very common <X Remove="@(X)" Condition="<cond on %(X.M)>" />
            // pattern. MSBuild semantics: iterate X, evaluate Condition per item, remove items
            // for which it's true. The general per-item-batched codegen would emit
            //   foreach (var batchItem in X) { if (cond) { itemsToRemove = HashSet<>(X.Select(Identity));
            //                                              X.RemoveAll(itemsToRemove.Contains); } }
            // which is both quadratic-ish AND crashes with "collection modified during
            // enumeration" the first time the condition is true. Replace it with a single
            // X.RemoveAll(candidate => cond) — which is what the user meant.
            {
                var removeDirect = !string.IsNullOrEmpty(item.Remove)
                    ? TryParseDirectItemRef(item.Remove) : null;
                if (string.IsNullOrEmpty(item.Include)
                    && removeDirect != null
                    && batch != null
                    && string.Equals(removeDirect, batch, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(removeDirect, TryCanonicalItemKey(item.ItemType), StringComparison.OrdinalIgnoreCase)
                    && !item.Metadata.Any())
                {
                    var removeTarget = ItemAccess(item.ItemType);
                    if (!string.IsNullOrEmpty(itemCondition)) {
                        sb.AppendLine($"{ind}{removeTarget}.RemoveAll(batchItem => {CompileCond(itemCondition, batch)});");
                    } else {
                        sb.AppendLine($"{ind}{removeTarget}.Clear();");
                    }
                    continue;
                }
            }

            var openCond = !string.IsNullOrEmpty(itemCondition) ? $"if ({CompileCond(itemCondition, batch)}) " : "";
            var target = ItemAccess(item.ItemType);
            var openedBatch = false;
            if (!string.IsNullOrEmpty(item.Include)) {
                var direct = TryParseDirectItemRef(item.Include);
                var projection = TryParseItemProjection(item.Include);
                var dynamicInclude = TryParseDynamicItemInclude(item.Include);
                var semicolonSplit = batch == null ? TryParsePropertySemicolonSplit(item.Include) : null;
                if (dynamicInclude is { } dynamic
                    && batch != null
                    && string.Equals(dynamic.ItemType, batch, StringComparison.OrdinalIgnoreCase))
                {
                    if (!openedBatch) {
                        sb.AppendLine($"{iind}foreach (var batchItem in {ItemAccess(batch)}.ToArray()) {{");
                        iind += "    ";
                        openedBatch = true;
                    }
                    sb.AppendLine($"{iind}{openCond}{{");
                    sb.AppendLine($"{iind}    foreach (var sourceItem in I.Get(batchItem.GetMetadata({CSharpLiteral(dynamic.MetadataName.ToLowerInvariant())}))) {{");
                    sb.AppendLine($"{iind}        var newItem = new Item(sourceItem.Identity);");
                    sb.AppendLine($"{iind}        sourceItem.CopyMetadataTo(newItem);");
                    foreach (var m in item.Metadata) {
                        var assign = $"newItem.SetMetadata({CSharpLiteral(m.Name.ToLowerInvariant())}, {CompileExpr(m.Value, batch)})";
                        if (!string.IsNullOrEmpty(m.Condition))
                            sb.AppendLine($"{iind}        if ({CompileCond(m.Condition, batch)}) {assign};");
                        else
                            sb.AppendLine($"{iind}        {assign};");
                    }
                    sb.AppendLine($"{iind}        {target}.Add(newItem);");
                    sb.AppendLine($"{iind}    }}");
                    sb.AppendLine($"{iind}}}");
                    if (openedBatch) sb.AppendLine($"{ind}}}");
                    continue;
                }
                if (projection != null
                    && (batch == null || string.Equals(projection.Value.ItemType, batch, StringComparison.OrdinalIgnoreCase)))
                {
                    var sourceEnumerable = string.Equals(TryCanonicalItemKey(item.ItemType), TryCanonicalItemKey(projection.Value.ItemType), StringComparison.OrdinalIgnoreCase)
                        ? $"{projection.Value.ItemsExpr}.ToArray()"
                        : projection.Value.ItemsExpr;
                    var sourceCond = !string.IsNullOrEmpty(itemCondition)
                        ? $"if ({(batch == null ? CompileCond(itemCondition) : CompileCond(itemCondition, batch).Replace("batchItem.", "sourceItem.", StringComparison.Ordinal))}) "
                        : "";
                    sb.AppendLine($"{iind}foreach (var sourceItem in {sourceEnumerable}) {{");
                    sb.AppendLine($"{iind}    {sourceCond}{{");
                    sb.AppendLine($"{iind}        var newItem = new Item({projection.Value.Selector});");
                    foreach (var m in item.Metadata) {
                        var value = batch == null
                            ? CompileExpr(m.Value)
                            : CompileExpr(m.Value, batch).Replace("batchItem.", "sourceItem.", StringComparison.Ordinal);
                        var assign = $"newItem.SetMetadata({CSharpLiteral(m.Name.ToLowerInvariant())}, {value})";
                        if (!string.IsNullOrEmpty(m.Condition)) {
                            var metadataCondition = batch == null
                                ? CompileCond(m.Condition)
                                : CompileCond(m.Condition, batch).Replace("batchItem.", "sourceItem.", StringComparison.Ordinal);
                            sb.AppendLine($"{iind}        if ({metadataCondition}) {assign};");
                        } else {
                            sb.AppendLine($"{iind}        {assign};");
                        }
                    }
                    sb.AppendLine($"{iind}        {target}.Add(newItem);");
                    sb.AppendLine($"{iind}    }}");
                    sb.AppendLine($"{iind}}}");
                    continue;
                }

                if (semicolonSplit != null) {
                    var sourceCond = !string.IsNullOrEmpty(itemCondition)
                        ? $"if ({CompileCond(itemCondition)}) "
                        : "";
                    sb.AppendLine($"{iind}foreach (var identity in new SemicolonSplit({semicolonSplit})) {{");
                    sb.AppendLine($"{iind}    {sourceCond}{{");
                    sb.AppendLine($"{iind}        var newItem = new Item(identity.ToString());");
                    foreach (var m in item.Metadata) {
                        var assign = $"newItem.SetMetadata({CSharpLiteral(m.Name.ToLowerInvariant())}, {CompileExpr(m.Value)})";
                        if (!string.IsNullOrEmpty(m.Condition))
                            sb.AppendLine($"{iind}        if ({CompileCond(m.Condition)}) {assign};");
                        else
                            sb.AppendLine($"{iind}        {assign};");
                    }
                    sb.AppendLine($"{iind}        {target}.Add(newItem);");
                    sb.AppendLine($"{iind}    }}");
                    sb.AppendLine($"{iind}}}");
                    continue;
                }

                // Snapshot the batch list before iterating so the body can safely mutate the
                // underlying list. Defensive — covers patterns we haven't special-cased above.
                if (batch != null) {
                    sb.AppendLine($"{iind}foreach (var batchItem in {ItemAccess(batch)}.ToArray()) {{");
                    iind += "    ";
                    openedBatch = true;
                }
                sb.AppendLine($"{iind}{openCond}{{");
                if (direct != null) {
                    // If the Include source is the SAME item type as the surrounding batch,
                    // the outer foreach already iterates each batchItem. Re-iterating `direct` would
                    // do N×N work and produce N² items per outer iteration (cascading to
                    // N⁴ items in downstream consumers). Use batchItem directly.
                    //   <X Include="@(Y)" Condition="<cond on %(Y.M)>"/>
                    // semantics is "per batchItem in Y: if cond then add THIS batchItem to X".
                    bool batchEqualsDirect = batch != null && string.Equals(batch, direct, StringComparison.OrdinalIgnoreCase);
                    if (batchEqualsDirect) {
                        sb.AppendLine($"{iind}    {{");
                        sb.AppendLine($"{iind}        var sourceItem = batchItem;");
                        sb.AppendLine($"{iind}        var newItem = new Item(sourceItem.Identity);");
                        sb.AppendLine($"{iind}        sourceItem.CopyMetadataTo(newItem);");
                        foreach (var m in item.Metadata) {
                            var assign = $"newItem.SetMetadata({CSharpLiteral(m.Name.ToLowerInvariant())}, {CompileExpr(m.Value, batch ?? direct)})";
                            if (!string.IsNullOrEmpty(m.Condition))
                                sb.AppendLine($"{iind}        if ({CompileCond(m.Condition, batch ?? direct)}) {assign};");
                            else
                                sb.AppendLine($"{iind}        {assign};");
                        }
                        sb.AppendLine($"{iind}        {target}.Add(newItem);");
                        sb.AppendLine($"{iind}    }}");
                    } else {
                        sb.AppendLine($"{iind}    foreach (var sourceItem in {ItemAccess(direct)}) {{");
                        sb.AppendLine($"{iind}        var newItem = new Item(sourceItem.Identity);");
                        sb.AppendLine($"{iind}        sourceItem.CopyMetadataTo(newItem);");
                        foreach (var m in item.Metadata) {
                            var assign = $"newItem.SetMetadata({CSharpLiteral(m.Name.ToLowerInvariant())}, {CompileExpr(m.Value, batch ?? direct)})";
                            if (!string.IsNullOrEmpty(m.Condition))
                                sb.AppendLine($"{iind}        if ({CompileCond(m.Condition, batch ?? direct)}) {assign};");
                            else
                                sb.AppendLine($"{iind}        {assign};");
                        }
                        sb.AppendLine($"{iind}        {target}.Add(newItem);");
                        sb.AppendLine($"{iind}    }}");
                    }
                } else if (IsPureLiteralNoSemicolon(item.Include)) {
                    sb.AppendLine($"{iind}    var newItem = new Item({CSharpLiteral(item.Include)});");
                    foreach (var m in item.Metadata) {
                        var assign = $"newItem.SetMetadata({CSharpLiteral(m.Name.ToLowerInvariant())}, {CompileExpr(m.Value, batch)})";
                        if (!string.IsNullOrEmpty(m.Condition))
                            sb.AppendLine($"{iind}    if ({CompileCond(m.Condition, batch)}) {assign};");
                        else
                            sb.AppendLine($"{iind}    {assign};");
                    }
                    sb.AppendLine($"{iind}    {target}.Add(newItem);");
                } else {
                    sb.AppendLine($"{iind}    foreach (var identity in new SemicolonSplit({CompileExpr(item.Include, batch)})) {{");
                    sb.AppendLine($"{iind}        var newItem = new Item(identity.ToString());");
                    foreach (var m in item.Metadata) {
                        var assign = $"newItem.SetMetadata({CSharpLiteral(m.Name.ToLowerInvariant())}, {CompileExpr(m.Value, batch)})";
                        if (!string.IsNullOrEmpty(m.Condition))
                            sb.AppendLine($"{iind}        if ({CompileCond(m.Condition, batch)}) {assign};");
                        else
                            sb.AppendLine($"{iind}        {assign};");
                    }
                    sb.AppendLine($"{iind}        {target}.Add(newItem);");
                    sb.AppendLine($"{iind}    }}");
                }
                sb.AppendLine($"{iind}}}");
            } else if (!string.IsNullOrEmpty(item.Remove)) {
                // Snapshot the batch list before iterating so the body can safely mutate the
                // underlying list. Defensive — covers patterns we haven't special-cased above.
                if (batch != null) {
                    sb.AppendLine($"{iind}foreach (var batchItem in {ItemAccess(batch)}.ToArray()) {{");
                    iind += "    ";
                    openedBatch = true;
                }
                var direct = TryParseDirectItemRef(item.Remove);
                sb.AppendLine($"{iind}{openCond}{{");
                if (direct != null) {
                    sb.AppendLine($"{iind}    var itemsToRemove = StringSet.FromItems({ItemAccess(direct)});");
                    sb.AppendLine($"{iind}    {target}.RemoveAll(item => itemsToRemove.Contains(item.Identity));");
                } else if (IsPureLiteralNoSemicolon(item.Remove) && !item.Remove.Contains('*') && !item.Remove.Contains('?')) {
                    sb.AppendLine($"{iind}    {target}.RemoveAll(item => string.Equals(item.Identity, {CSharpLiteral(item.Remove)}, StringComparison.OrdinalIgnoreCase));");
                } else {
                    sb.AppendLine($"{iind}    var itemsToRemove = StringSet.FromSemicolonList({CompileExpr(item.Remove, batch)});");
                    sb.AppendLine($"{iind}    {target}.RemoveAll(item => itemsToRemove.Contains(item.Identity));");
                }
                sb.AppendLine($"{iind}}}");
            } else if (item.Metadata.Any()) {
                var updateBatch = batch ?? TryCanonicalItemKey(item.ItemType);
                sb.AppendLine($"{iind}foreach (var batchItem in {ItemAccess(updateBatch)}) {{");
                iind += "    ";
                if (!string.IsNullOrEmpty(itemCondition)) {
                    sb.AppendLine($"{iind}if ({CompileCond(itemCondition, updateBatch)}) {{");
                    iind += "    ";
                }
                foreach (var m in item.Metadata) {
                    var assign = $"batchItem.SetMetadata({CSharpLiteral(m.Name.ToLowerInvariant())}, {CompileExpr(m.Value, updateBatch)})";
                    if (!string.IsNullOrEmpty(m.Condition))
                        sb.AppendLine($"{iind}if ({CompileCond(m.Condition, updateBatch)}) {assign};");
                    else
                        sb.AppendLine($"{iind}{assign};");
                }
                if (!string.IsNullOrEmpty(itemCondition)) {
                    iind = iind.Substring(4);
                    sb.AppendLine($"{iind}}}");
                }
                sb.AppendLine($"{ind}}}");
            }
            if (openedBatch) sb.AppendLine($"{ind}}}");
        }
        sb.AppendLine($"{outer}}}");
    }

    static bool TryMergeAdjacentItemAdditions(
        IReadOnlyList<ProjectItemGroupTaskItemInstance> items,
        int start,
        out int count,
        out string mergedCondition)
    {
        count = 1;
        mergedCondition = items[start].Condition;
        var first = items[start];
        if (string.IsNullOrEmpty(first.Include)
            || !string.IsNullOrEmpty(first.Remove)
            || string.IsNullOrEmpty(first.Condition))
        {
            return false;
        }

        var end = start + 1;
        while (end < items.Count && HasSameItemAddBody(first, items[end])) end++;
        for (var candidateCount = end - start; candidateCount >= 2; candidateCount--) {
            var conditions = new string[candidateCount];
            for (var i = 0; i < candidateCount; i++) conditions[i] = items[start + i].Condition;
            if (TryBuildMutuallyExclusiveConditionOr(conditions, out mergedCondition)) {
                count = candidateCount;
                return true;
            }
        }

        mergedCondition = first.Condition;
        return false;
    }

    static bool HasSameItemAddBody(ProjectItemGroupTaskItemInstance first, ProjectItemGroupTaskItemInstance next) {
        if (!string.Equals(first.ItemType, next.ItemType, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(first.Include, next.Include, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(next.Remove)) return false;

        var firstMetadata = first.Metadata.ToList();
        var nextMetadata = next.Metadata.ToList();
        if (firstMetadata.Count != nextMetadata.Count) return false;
        for (var i = 0; i < firstMetadata.Count; i++) {
            if (!string.Equals(firstMetadata[i].Name, nextMetadata[i].Name, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(firstMetadata[i].Value, nextMetadata[i].Value, StringComparison.Ordinal)) return false;
            if (!string.Equals(firstMetadata[i].Condition, nextMetadata[i].Condition, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    sealed record ParsedConditionTerm(string Normalized, string? MetadataRef, string? LiteralValue);

    static bool TryBuildMutuallyExclusiveConditionOr(IReadOnlyList<string> conditions, out string mergedCondition) {
        mergedCondition = "";
        var parsed = new List<List<ParsedConditionTerm>>(conditions.Count);
        foreach (var condition in conditions) {
            if (string.IsNullOrWhiteSpace(condition)) return false;
            var terms = SplitConditionAndTerms(condition)
                .Select(ParseConditionTerm)
                .ToList();
            if (terms.Count == 0) return false;
            parsed.Add(terms);
        }

        foreach (var candidate in parsed[0].Where(t => t.MetadataRef != null).Select(t => t.MetadataRef!).Distinct(StringComparer.OrdinalIgnoreCase)) {
            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? sharedRemainder = null;
            var ok = true;
            foreach (var terms in parsed) {
                var matches = terms.Where(t => string.Equals(t.MetadataRef, candidate, StringComparison.OrdinalIgnoreCase)).ToList();
                var literalValue = matches.Count == 1 ? matches[0].LiteralValue : null;
                if (literalValue == null || !values.Add(literalValue)) {
                    ok = false;
                    break;
                }

                var remainder = string.Join("&&", terms
                    .Where(t => !ReferenceEquals(t, matches[0]))
                    .Select(t => t.Normalized)
                    .OrderBy(t => t, StringComparer.Ordinal));
                sharedRemainder ??= remainder;
                if (!string.Equals(sharedRemainder, remainder, StringComparison.Ordinal)) {
                    ok = false;
                    break;
                }
            }

            if (ok) {
                mergedCondition = string.Join(" OR ", conditions.Select(c => $"({c})"));
                return true;
            }
        }

        return false;
    }

    static IEnumerable<string> SplitConditionAndTerms(string condition) {
        var start = 0;
        var inQuote = false;
        for (var i = 0; i < condition.Length; i++) {
            if (condition[i] == '\'') {
                inQuote = !inQuote;
                continue;
            }
            if (inQuote || i + 3 > condition.Length) continue;
            if (!condition.AsSpan(i, 3).Equals("AND".AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;

            var beforeOk = i == 0 || !char.IsLetterOrDigit(condition[i - 1]);
            var afterOk = i + 3 == condition.Length || !char.IsLetterOrDigit(condition[i + 3]);
            if (!beforeOk || !afterOk) continue;

            var term = condition.Substring(start, i - start).Trim();
            if (term.Length != 0) yield return term;
            i += 2;
            start = i + 1;
        }

        var last = condition.Substring(start).Trim();
        if (last.Length != 0) yield return last;
    }

    static ParsedConditionTerm ParseConditionTerm(string term) {
        var normalized = NormalizeConditionTerm(term);
        var equals = IndexOfOutsideQuotes(term, "==");
        if (equals < 0) return new ParsedConditionTerm(normalized, null, null);

        var left = term.Substring(0, equals).Trim();
        var right = term.Substring(equals + 2).Trim();
        if (TryParseQuotedMetadataRef(left, out var metadataRef) && TryParseQuotedLiteral(right, out var literalValue)) {
            return new ParsedConditionTerm(normalized, metadataRef, literalValue);
        }
        if (TryParseQuotedMetadataRef(right, out metadataRef) && TryParseQuotedLiteral(left, out literalValue)) {
            return new ParsedConditionTerm(normalized, metadataRef, literalValue);
        }
        return new ParsedConditionTerm(normalized, null, null);
    }

    static int IndexOfOutsideQuotes(string text, string value) {
        var inQuote = false;
        for (var i = 0; i <= text.Length - value.Length; i++) {
            if (text[i] == '\'') inQuote = !inQuote;
            if (!inQuote && text.AsSpan(i, value.Length).Equals(value.AsSpan(), StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    static bool TryParseQuotedMetadataRef(string text, out string metadataRef) {
        metadataRef = "";
        if (!TryParseQuotedLiteral(text, out var literal)) return false;
        literal = literal.Trim();
        if (!literal.StartsWith("%(", StringComparison.Ordinal) || !literal.EndsWith(")", StringComparison.Ordinal)) return false;
        metadataRef = literal.Substring(2, literal.Length - 3).Trim().ToLowerInvariant();
        return metadataRef.Length != 0;
    }

    static bool TryParseQuotedLiteral(string text, out string literal) {
        literal = "";
        text = text.Trim();
        if (text.Length < 2 || text[0] != '\'' || text[^1] != '\'') return false;
        literal = text.Substring(1, text.Length - 2);
        return true;
    }

    static string NormalizeConditionTerm(string term) {
        var sb = new StringBuilder(term.Length);
        var inQuote = false;
        foreach (var ch in term) {
            if (ch == '\'') {
                inQuote = !inQuote;
                sb.Append(ch);
            } else if (inQuote) {
                sb.Append(ch);
            } else if (!char.IsWhiteSpace(ch)) {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }
        return sb.ToString();
    }

    // Returns the lowercase form of an item type if we have a canonical mapping for it
    // (used as the self-batch hint key in EmitItemGroup). Returns the lowercase even
    // when not in the registry, since ItemAccess will fall through to I.Get(...) anyway.
    static string TryCanonicalItemKey(string itemType) => itemType.ToLowerInvariant();

    static readonly HashSet<string> KnownTasks = new(StringComparer.Ordinal) {
        "Message","MakeDir","WriteLinesToFile","Touch","Delete","Copy","Error","Warning",
        "ConvertToAbsolutePath","RemoveDir","CreateProperty","CreateItem","FindUnderPath","ReadLinesFromFile",
        "Exec","Hash","MSBuildInternalMessage","NetSdkWarning","MSBuild",
        "SetRidAgnosticValueForProjects",
    };
    static bool TaskTakesOutputs(string name) => name switch {
        "MakeDir" or "Touch" or "Delete" or "Copy" or "ConvertToAbsolutePath"
            or "CreateProperty" or "CreateItem" or "FindUnderPath" or "ReadLinesFromFile"
            or "Exec" or "Hash" => true,
        _ => false,
    };
    static bool ForceLocalTaskImplementation(string name) => name is
        "MSBuildInternalMessage" or "NetSdkWarning" or "MSBuild"
        or "SetRidAgnosticValueForProjects";

    static void EmitTask(StringBuilder sb, ProjectTaskInstance task) {
        var ind = "        ";
        var strs = new List<string?> { task.Condition };
        foreach (var kv in task.Parameters) strs.Add(kv.Value);
        // For Tasks, the @(X) refs in the same scope can serve as the batch context when
        // metadata is unqualified (e.g. <Copy SourceFiles="@(X)" DestinationFiles="$(O)\%(Filename)">).
        string? batch = InferBatchType(strs, itemRefScopeExprs: strs);
        if (batch != null) {
            sb.AppendLine($"{ind}foreach (var batchItem in {ItemAccess(batch)}.Count > 0 ? (IEnumerable<Item>){ItemAccess(batch)} : new[] {{ new Item(\"\") }}) {{");
            ind += "    ";
        }
        var openCond = !string.IsNullOrEmpty(task.Condition) ? $"if ({CompileCond(task.Condition, batch)}) " : "";
        var realTaskMeta = task.Name != "CallTarget" && !ForceLocalTaskImplementation(task.Name)
            ? GetTaskMeta(task.Name)
            : null;
        sb.AppendLine($"{ind}{openCond}{{");
        if (realTaskMeta is TaskMetadataLoader.TaskMeta realMeta) {
            EmitTypedTaskInvocation(sb, ind, task, realMeta, batch);
            sb.AppendLine($"{ind}}}");
            if (batch != null) sb.AppendLine($"        }}");
            return;
        }
        sb.AppendLine($"{ind}    using var taskLog = Log.Task({CSharpLiteral(task.Name)});");

        // CallTarget: dynamic target invocation via the Targets.Run(string) dispatcher.
        // The Targets parameter is a `;`-separated list which may include $(Prop) refs that
        // expand at runtime. Each named target's own `_ran` flag still gates execute-once.
        // TargetOutputs and RunEachTargetSeparately parameters are ignored for now.
        if (task.Name == "CallTarget") {
            string? targetsExpr = null;
            foreach (var kv in task.Parameters) {
                if (kv.Key.Equals("Targets", StringComparison.OrdinalIgnoreCase)) {
                    targetsExpr = CompileExpr(kv.Value, batch);
                    break;
                }
            }
            if (targetsExpr != null) {
                if (TryUnquoteStringLiteral(targetsExpr, out var literalTargets) && !literalTargets.Contains(';')) {
                    sb.AppendLine($"{ind}    await Run({CSharpLiteral(literalTargets.Trim())});");
                } else {
                    sb.AppendLine($"{ind}    var callTargets = {targetsExpr};");
                    sb.AppendLine($"{ind}    var callTargetPos = 0;");
                    sb.AppendLine($"{ind}    while (callTargetPos <= callTargets.Length) {{");
                    sb.AppendLine($"{ind}        var callTargetEnd = callTargets.IndexOf(';', callTargetPos);");
                    sb.AppendLine($"{ind}        if (callTargetEnd < 0) callTargetEnd = callTargets.Length;");
                    sb.AppendLine($"{ind}        var callTargetName = callTargets.AsSpan(callTargetPos, callTargetEnd - callTargetPos).Trim().ToString();");
                    sb.AppendLine($"{ind}        if (callTargetName.Length != 0) await Run(callTargetName);");
                    sb.AppendLine($"{ind}        callTargetPos = callTargetEnd + 1;");
                    sb.AppendLine($"{ind}    }}");
                }
            }
        } else if (ForceLocalTaskImplementation(task.Name)) {
            EmitLocalTaskInvocation(sb, ind, task, batch);
        } else if (!KnownTasks.Contains(task.Name)) {
            sb.AppendLine($"{ind}    Tasks.NotImplemented({CSharpLiteral(task.Name)});");
        } else {
            EmitLocalTaskInvocation(sb, ind, task, batch);
        }
        sb.AppendLine($"{ind}}}");
        if (batch != null) sb.AppendLine($"        }}");
    }

    static void EmitLocalTaskInvocation(StringBuilder sb, string ind, ProjectTaskInstance task, string? batch) {
        if (task.Parameters.Count == 0) {
            sb.AppendLine($"{ind}    var parameters = ParamList.Empty;");
        } else if (task.Parameters.Count == 1) {
            var kv = task.Parameters.First();
            sb.AppendLine($"{ind}    var parameters = new ParamList({CSharpLiteral(kv.Key)}, {CompileExpr(kv.Value, batch)});");
        } else {
            sb.AppendLine($"{ind}    var parameters = new ParamList(new (string Key, string Value)[] {{");
            foreach (var kv in task.Parameters)
                sb.AppendLine($"{ind}        ({CSharpLiteral(kv.Key)}, {CompileExpr(kv.Value, batch)}),");
            sb.AppendLine($"{ind}    }});");
        }
        if (TaskTakesOutputs(task.Name)) {
            if (task.Outputs.Count > 0) {
                if (task.Outputs.Count == 1) {
                    var o = task.Outputs.First();
                    if (o is ProjectTaskOutputItemInstance oi)
                        sb.AppendLine($"{ind}    var outputs = new OutputList({CSharpLiteral(oi.TaskParameter)}, {CSharpLiteral(oi.ItemType.ToLowerInvariant())}, null);");
                    else if (o is ProjectTaskOutputPropertyInstance op)
                        sb.AppendLine($"{ind}    var outputs = new OutputList({CSharpLiteral(op.TaskParameter)}, {CSharpLiteral(op.PropertyName.ToLowerInvariant())}, \"property\");");
                    else
                        sb.AppendLine($"{ind}    var outputs = new OutputList(Array.Empty<(string Key, string itemName, string? metadataName)>());");
                } else {
                    sb.AppendLine($"{ind}    var outputs = new OutputList(new (string Key, string itemName, string? metadataName)[] {{");
                    foreach (var o in task.Outputs) {
                        if (o is ProjectTaskOutputItemInstance oi)
                            sb.AppendLine($"{ind}        ({CSharpLiteral(oi.TaskParameter)}, {CSharpLiteral(oi.ItemType.ToLowerInvariant())}, null),");
                        else if (o is ProjectTaskOutputPropertyInstance op)
                            sb.AppendLine($"{ind}        ({CSharpLiteral(op.TaskParameter)}, {CSharpLiteral(op.PropertyName.ToLowerInvariant())}, \"property\"),");
                    }
                    sb.AppendLine($"{ind}    }});");
                }
                sb.AppendLine($"{ind}    Tasks.{task.Name}(parameters, outputs);");
            } else {
                sb.AppendLine($"{ind}    Tasks.{task.Name}(parameters, null);");
            }
        } else {
            sb.AppendLine($"{ind}    Tasks.{task.Name}(parameters);");
        }
    }

    sealed record GeneratedTaskArg(string ParameterName, string XmlName, string MethodArgName, string TypeName, string RawValue, string ValueExpr, TaskMetadataLoader.PropertyMeta Property, bool RequiresCallerValue);

    static readonly List<string> _generatedTaskHelpers = new();
    static int _generatedTaskHelperCounter;

    static void ResetGeneratedTaskHelpers() {
        _generatedTaskHelpers.Clear();
        _generatedTaskHelperCounter = 0;
    }

    // Emit a call to a generated Tasks.<Task>_<n>(...) helper. The helper owns the repetitive
    // TaskRunner.Create/Set*/Execute plumbing, while the target body stays responsible for
    // copying task outputs back into generated P/I state.
    static void EmitTypedTaskInvocation(StringBuilder sb, string ind, ProjectTaskInstance task, TaskMetadataLoader.TaskMeta meta, string? batch) {
        var shortName = meta.FullTypeName.Contains('.')
            ? meta.FullTypeName.Substring(meta.FullTypeName.LastIndexOf('.') + 1)
            : meta.FullTypeName;

        // Build a name → PropertyMeta map (case-insensitive).
        var propMap = new Dictionary<string, TaskMetadataLoader.PropertyMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in meta.Properties) propMap[p.Name] = p;

        var args = new List<GeneratedTaskArg>();
        foreach (var kv in task.Parameters) {
            if (!propMap.TryGetValue(kv.Key, out var pm)) {
                sb.AppendLine($"{ind}    // skipping unknown parameter '{kv.Key}' (not on {meta.FullTypeName})");
                continue;
            }
            if (!pm.CanWrite) {
                sb.AppendLine($"{ind}    // skipping read-only parameter '{kv.Key}' on {meta.FullTypeName}");
                continue;
            }
            var methodArgName = $"p{args.Count}";
            var rawValue = kv.Value ?? "";
            var valueExpr = CompileExpr(rawValue, batch);
            var typeName = "string";
            var direct = TryParseDirectItemRef(rawValue);
            if ((pm.PropertyTypeShort == "ITaskItem" || pm.PropertyTypeShort == "ITaskItem[]") && direct != null) {
                typeName = "List<Item>";
                valueExpr = ItemAccess(direct);
            }
            // Most generated task arguments are just expressions over global generated state
            // (`P.X`, `I.Y`, literals, and LINQ over item lists), so the helper can read them
            // directly and avoid a long p0/p1/... signature. Batched task metadata references
            // depend on the target body's current `batchItem`, so keep those rare values as caller
            // parameters.
            var requiresCallerValue = batch != null && ValueExprUsesCurrentBatchItem(valueExpr);
            args.Add(new GeneratedTaskArg(kv.Key, pm.Name, methodArgName, typeName, rawValue, valueExpr, pm, requiresCallerValue));
        }

        var helperName = RegisterGeneratedTaskHelper(task.Name, shortName, args);
        var callerArgs = args.Where(a => a.RequiresCallerValue).Select(a => a.ValueExpr);
        sb.AppendLine($"{ind}    var task = Tasks.{helperName}({string.Join(", ", callerArgs)});");

        // Outputs: typed Get calls on the response.
        foreach (var o in task.Outputs) {
            string outParamName = o switch {
                ProjectTaskOutputItemInstance oi => oi.TaskParameter,
                ProjectTaskOutputPropertyInstance op => op.TaskParameter,
                _ => ""
            };
            if (!propMap.TryGetValue(outParamName, out var pm)) {
                sb.AppendLine($"{ind}    // skipping unknown output parameter '{outParamName}' on {meta.FullTypeName}");
                continue;
            }
            EmitOutputResponseGet(sb, ind + "    ", "task", outParamName, pm, o);
        }
    }

    static bool ValueExprUsesCurrentBatchItem(string valueExpr) =>
        valueExpr.Contains("batchItem.GetMetadata(", StringComparison.Ordinal)
        || valueExpr.Contains("batchItem.HasMetadata(", StringComparison.Ordinal);

    static string RegisterGeneratedTaskHelper(string logName, string shortName, List<GeneratedTaskArg> args) {
        var helperName = $"{SanitizeIdent(shortName)}_{++_generatedTaskHelperCounter:D3}";
        var callerArgs = args.Where(a => a.RequiresCallerValue).ToArray();
        var body = new StringBuilder();
        body.Append($"    public static TaskRunner.TaskInstance {helperName}(");
        body.Append(string.Join(", ", callerArgs.Select(a => $"{a.TypeName} {a.MethodArgName}")));
        body.AppendLine(") {");
        body.AppendLine($"        var task = TaskRunner.Create({CSharpLiteral(shortName)});");
        foreach (var arg in args)
            EmitParameterRequestSetFromArg(body, "        ", "task", arg);
        body.AppendLine("        TaskRunner.ExecuteAndThrowOnError(task);");
        body.AppendLine("        return task;");
        body.AppendLine("    }");
        _generatedTaskHelpers.Add(body.ToString());
        return helperName;
    }

    static void EmitGeneratedTaskHelpers(StringBuilder sb) {
        if (_generatedTaskHelpers.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("static partial class Tasks {");
        foreach (var helper in _generatedTaskHelpers) {
            sb.Append(helper);
            sb.AppendLine();
        }
        sb.AppendLine("}");
    }

    // Emit a `TaskRunner.SetX(task, "Foo", <value>);` line that sets the destination
    // task property in the right CLR shape.
    static void EmitParameterRequestSet(StringBuilder sb, string ind, string reqVar, string xmlName, string? paramValue, TaskMetadataLoader.PropertyMeta pm, string? batch) {
        paramValue ??= "";
        var key = CSharpLiteral(pm.Name);
        switch (pm.PropertyTypeShort) {
            case "string":
                sb.AppendLine($"{ind}TaskRunner.SetStringIfNotNullOrEmpty({reqVar}, {key}, {CompileExpr(paramValue, batch)});");
                return;
            case "bool":
                sb.AppendLine($"{ind}TaskRunner.SetBoolIfNotNullOrEmpty({reqVar}, {key}, {CompileExpr(paramValue, batch)});");
                return;
            case "int":
                sb.AppendLine($"{ind}TaskRunner.SetIntIfNotNullOrEmpty({reqVar}, {key}, {CompileExpr(paramValue, batch)});");
                return;
            case "int?":
                sb.AppendLine($"{ind}TaskRunner.SetIntIfNotNullOrEmpty({reqVar}, {key}, {CompileExpr(paramValue, batch)});");
                return;
            case "long":
                sb.AppendLine($"{ind}TaskRunner.SetLongIfNotNullOrEmpty({reqVar}, {key}, {CompileExpr(paramValue, batch)});");
                return;
            case "long?":
                sb.AppendLine($"{ind}TaskRunner.SetLongIfNotNullOrEmpty({reqVar}, {key}, {CompileExpr(paramValue, batch)});");
                return;
            case "double":
                sb.AppendLine($"{ind}TaskRunner.SetDoubleIfNotNullOrEmpty({reqVar}, {key}, {CompileExpr(paramValue, batch)});");
                return;
            case "double?":
                sb.AppendLine($"{ind}TaskRunner.SetDoubleIfNotNullOrEmpty({reqVar}, {key}, {CompileExpr(paramValue, batch)});");
                return;
            case "ITaskItem": {
                var direct = TryParseDirectItemRef(paramValue);
                if (direct != null) {
                    var access = ItemAccess(direct);
                    sb.AppendLine($"{ind}if ({access}.Count > 0) TaskRunner.SetItem({reqVar}, {key}, ItemSerde.OneSpec({access}[0]));");
                } else {
                    var s = CompileExpr(paramValue, batch);
                    sb.AppendLine($"{ind}TaskRunner.SetItemFromString({reqVar}, {key}, {s});");
                }
                return;
            }
            case "ITaskItem[]": {
                var direct = TryParseDirectItemRef(paramValue);
                if (direct != null) {
                    sb.AppendLine($"{ind}TaskRunner.SetItems({reqVar}, {key}, ItemSerde.ToSpecs({ItemAccess(direct)}));");
                } else if (TryParseItemProjection(paramValue) is { } projection) {
                    sb.AppendLine($"{ind}TaskRunner.SetItems({reqVar}, {key}, ItemSerde.ToSpecsWithIdentity({projection.ItemsExpr}, sourceItem => {projection.Selector}));");
                } else if (TryParseSimpleMetadataProjection(paramValue) is { } simpleProjection) {
                    sb.AppendLine($"{ind}TaskRunner.SetItems({reqVar}, {key}, ItemSerde.ToSpecsWithIdentity({ItemAccess(simpleProjection.ItemType)}, sourceItem => sourceItem.GetMetadata({CSharpLiteral(simpleProjection.MetadataName.ToLowerInvariant())})));");
                } else {
                    sb.AppendLine($"{ind}TaskRunner.SetItems({reqVar}, {key}, ItemSerde.SpecsFromScalar({CompileExpr(paramValue, batch)}));");
                }
                return;
            }
            case "string[]":
                sb.AppendLine($"{ind}TaskRunner.SetStringsIfNotNullOrEmpty({reqVar}, {key}, {CompileExpr(paramValue, batch)});");
                return;
            default:
                // Unsupported CLR type — fall through to string assignment. The in-proc task
                // runner will either consume it as string or fail; we'd rather surface mismatches
                // than silently drop the assignment.
                sb.AppendLine($"{ind}TaskRunner.SetStringIfNotNullOrEmpty({reqVar}, {key}, {CompileExpr(paramValue, batch)});");
                return;
        }
        _ = xmlName;
    }

    static void EmitParameterRequestSetFromArg(StringBuilder sb, string ind, string reqVar, GeneratedTaskArg arg) {
        var key = CSharpLiteral(arg.XmlName);
        var value = arg.RequiresCallerValue ? arg.MethodArgName : arg.ValueExpr;
        switch (arg.Property.PropertyTypeShort) {
            case "string":
                sb.AppendLine($"{ind}TaskRunner.SetStringIfNotNullOrEmpty({reqVar}, {key}, {value});");
                return;
            case "bool":
                sb.AppendLine($"{ind}TaskRunner.SetBoolIfNotNullOrEmpty({reqVar}, {key}, {value});");
                return;
            case "int":
            case "int?":
                sb.AppendLine($"{ind}TaskRunner.SetIntIfNotNullOrEmpty({reqVar}, {key}, {value});");
                return;
            case "long":
            case "long?":
                sb.AppendLine($"{ind}TaskRunner.SetLongIfNotNullOrEmpty({reqVar}, {key}, {value});");
                return;
            case "double":
            case "double?":
                sb.AppendLine($"{ind}TaskRunner.SetDoubleIfNotNullOrEmpty({reqVar}, {key}, {value});");
                return;
            case "ITaskItem":
                if (arg.TypeName == "List<Item>")
                    sb.AppendLine($"{ind}if ({value}.Count > 0) TaskRunner.SetItem({reqVar}, {key}, ItemSerde.OneSpec({value}[0]));");
                else
                    sb.AppendLine($"{ind}TaskRunner.SetItemFromString({reqVar}, {key}, {value});");
                return;
            case "ITaskItem[]":
                if (arg.TypeName == "List<Item>")
                    sb.AppendLine($"{ind}TaskRunner.SetItems({reqVar}, {key}, ItemSerde.ToSpecs({value}));");
                else if (!arg.RequiresCallerValue && TryParseItemProjection(arg.RawValue) is { } projection)
                    sb.AppendLine($"{ind}TaskRunner.SetItems({reqVar}, {key}, ItemSerde.ToSpecsWithIdentity({projection.ItemsExpr}, sourceItem => {projection.Selector}));");
                else if (!arg.RequiresCallerValue && TryParseSimpleMetadataProjection(arg.RawValue) is { } simpleProjection)
                    sb.AppendLine($"{ind}TaskRunner.SetItems({reqVar}, {key}, ItemSerde.ToSpecsWithIdentity({ItemAccess(simpleProjection.ItemType)}, sourceItem => sourceItem.GetMetadata({CSharpLiteral(simpleProjection.MetadataName.ToLowerInvariant())})));");
                else
                    sb.AppendLine($"{ind}TaskRunner.SetItems({reqVar}, {key}, ItemSerde.SpecsFromScalar({value}));");
                return;
            case "string[]":
                sb.AppendLine($"{ind}TaskRunner.SetStringsIfNotNullOrEmpty({reqVar}, {key}, {value});");
                return;
            default:
                sb.AppendLine($"{ind}TaskRunner.SetStringIfNotNullOrEmpty({reqVar}, {key}, {value});");
                return;
        }
    }

    // Emit a `TaskRunner.GetX(task, ...)` + write to I.<item> or P.<prop> for one Output.
    static void EmitOutputResponseGet(StringBuilder sb, string ind, string respVar, string xmlName, TaskMetadataLoader.PropertyMeta pm, ProjectTaskInstanceChild o) {
        var key = CSharpLiteral(pm.Name);
        if (o is ProjectTaskOutputItemInstance oitm) {
            var target = ItemAccess(oitm.ItemType);
            if (pm.PropertyTypeShort == "ITaskItem[]")
                sb.AppendLine($"{ind}{target}.AddRange(ItemSerde.FromSpecs(TaskRunner.GetItems({respVar}, {key})));");
            else if (pm.PropertyTypeShort == "ITaskItem")
                sb.AppendLine($"{ind}{target}.AddRange(ItemSerde.FromSpecs(TaskRunner.GetItems({respVar}, {key})));");
            else if (pm.PropertyTypeShort == "string[]")
                sb.AppendLine($"{ind}foreach (var outputValue in TaskRunner.GetStrings({respVar}, {key})) {target}.Add(new Item(outputValue == null ? \"\" : outputValue.Replace(\";\", \"%3B\")));");
            else
                sb.AppendLine($"{ind}{{ var outputValue = TaskRunner.GetString({respVar}, {key}); if (!string.IsNullOrEmpty(outputValue)) {target}.Add(new Item(outputValue)); }}");
        } else if (o is ProjectTaskOutputPropertyInstance optr) {
            string write;
            if (TryCanonicalProp(optr.PropertyName.ToLowerInvariant(), out var canon)) write = $"P.{canon} = ";
            else write = $"P.SetExtra({CSharpLiteral(optr.PropertyName.ToLowerInvariant())}, ";
            string trailer = TryCanonicalProp(optr.PropertyName.ToLowerInvariant(), out _) ? ";" : ");";

            if (pm.PropertyTypeShort == "ITaskItem[]")
                sb.AppendLine($"{ind}{write}string.Join(\";\", System.Linq.Enumerable.Select(TaskRunner.GetItems({respVar}, {key}), outputItem => outputItem.Identity)){trailer}");
            else if (pm.PropertyTypeShort == "string[]")
                sb.AppendLine($"{ind}{write}string.Join(\";\", TaskRunner.GetStrings({respVar}, {key})){trailer}");
            else if (pm.PropertyTypeShort == "bool")
                sb.AppendLine($"{ind}{write}(TaskRunner.GetString({respVar}, {key})){trailer}");
            else
                sb.AppendLine($"{ind}{write}TaskRunner.GetString({respVar}, {key}){trailer}");
        }
    }

    // Helpers ------------------------------------------------------

    public static string ItemAccess(string itemName) {
        return TryCanonicalItem(itemName.ToLowerInvariant(), out var canon)
            ? $"I.{canon}"
            : $"I.Get(\"{itemName.ToLowerInvariant()}\")";
    }

    static string SetProperty(string name, string valueExpr) {
        var lower = name.ToLowerInvariant();
        return TryCanonicalProp(lower, out var canon)
            ? $"P.{canon} = {valueExpr}"
            : $"P.SetExtra(\"{lower}\", {valueExpr})";
    }

    static string CompileCond(string? cond, string? batchItemType = null) {
        if (string.IsNullOrEmpty(cond)) return "true";
        var compiled = CondCompiler.TryCompile(cond, batchItemType);
        if (compiled == null) throw new InvalidOperationException($"uncompilable condition: {cond}");
        return compiled;
    }

    // First tries CondCompiler directly. If that throws (uncompilable construct), asks
    // MSBuild's evaluator to ExpandString the raw condition — that statically resolves
    // $(X), @(X), $([MSBuild]::F(...)), $(X.Method(...)), etc. against the project's
    // current property/item state — then tries CondCompiler again on the simpler result.
    // This catches the common case where MSBuild's own evaluator can simplify a condition
    // that uses property functions or instance methods, even though our limited CondCompiler
    // can't compile the raw form.
    //
    // The static result is authoritative for properties not mutated by target bodies.
    // For mutated ones it may be incorrect but the worst outcome is "skipped target whose
    // condition would have been true at runtime" — which we accept as a best-effort
    // optimization for now.
    static string CompileCondWithFallback(string? cond, ProjectInstance instance) {
        if (string.IsNullOrEmpty(cond)) return "true";
        try {
            return CompileCond(cond);
        } catch {
            string expanded;
            try { expanded = instance.ExpandString(cond); }
            catch { throw new InvalidOperationException($"uncompilable condition: {cond}"); }
            var compiled = CondCompiler.TryCompile(expanded);
            if (compiled == null)
                throw new InvalidOperationException($"uncompilable condition (even after ExpandString → \"{expanded}\"): {cond}");
            return compiled;
        }
    }

    // Returns the logical negation of a bool expression. Avoids `!!` for the common case
    // where the input already starts with `!` (CondCompiler emits `!string.IsNullOrEmpty(...)`
    // for `'$(X)' != ''`, and we wrap target Conditions in `if (!cond) return;`). CondCompiler's
    // output is always self-contained — top-level `!` applies to the whole expression — so
    // peeling it off is sound.
    static string NegateBoolExpr(string cond) {
        cond = cond.TrimStart();
        if (cond.StartsWith("!") && !cond.StartsWith("!=")) {
            return cond.Substring(1).TrimStart();
        }
        return "!" + cond;
    }

    static string CompileExpr(string? expr, string? batchItemType = null) {
        if (string.IsNullOrEmpty(expr)) return "\"\"";
        var compiled = ExprCompiler.TryCompile(expr, batchItemType);
        if (compiled == null) throw new InvalidOperationException($"uncompilable expression: {expr}");
        return compiled;
    }

    static (HashSet<string>, bool) ScanMetadataRefs(string? s) {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasUnq = false;
        if (string.IsNullOrEmpty(s)) return (types, false);
        int i = 0;
        while (i < s.Length - 1) {
            // Skip the body of an `@(X -> '...')` transform: any `%(...)` inside is part of
            // the transform expression, not a batching reference against the outer scope.
            // Same for `@(X, ',')` separator literals.
            if (s[i] == '@' && s[i + 1] == '(') {
                int depth = 1; int j = i + 2;
                while (j < s.Length && depth > 0) {
                    if (s[j] == '(') depth++;
                    else if (s[j] == ')') depth--;
                    if (depth > 0) j++;
                }
                if (j >= s.Length) break;
                i = j + 1;
                continue;
            }
            if (s[i] == '%' && s[i + 1] == '(') {
                int j = s.IndexOf(')', i + 2);
                if (j < 0) break;
                var inner = s.Substring(i + 2, j - i - 2);
                int dot = inner.IndexOf('.');
                if (dot >= 0) types.Add(inner.Substring(0, dot));
                else hasUnq = true;
                i = j + 1;
            } else i++;
        }
        return (types, hasUnq);
    }

    // Scan for direct `@(X)` and `@(X->...)` item-list references — used for batch-context
    // inference when an expression has unqualified `%(Meta)` refs but no qualified `%(X.M)`.
    // E.g. a Copy task with SourceFiles="@(Src)" and DestinationFiles="$(Out)\%(Filename)"
    // batches over Src.
    static HashSet<string> ScanItemListRefs(IEnumerable<string?> exprs) {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in exprs) ScanItemListRefsInto(s, refs);
        return refs;
    }
    static void ScanItemListRefsInto(string? s, HashSet<string> sink) {
        if (string.IsNullOrEmpty(s)) return;
        int i = 0;
        while (i < s.Length - 1) {
            if (s[i] == '@' && s[i + 1] == '(') {
                int depth = 1; int j = i + 2;
                while (j < s.Length && depth > 0) {
                    if (s[j] == '(') depth++;
                    else if (s[j] == ')') depth--;
                    if (depth > 0) j++;
                }
                if (j >= s.Length) break;
                var inner = s.Substring(i + 2, j - i - 2);
                // Strip transforms/separators — everything after `->` or `,`.
                int sep = inner.IndexOf("->", StringComparison.Ordinal);
                int comma = inner.IndexOf(',');
                int end = inner.Length;
                if (sep >= 0) end = Math.Min(end, sep);
                if (comma >= 0) end = Math.Min(end, comma);
                var name = inner.Substring(0, end).Trim();
                bool ok = name.Length > 0;
                foreach (var c in name) if (!(char.IsLetterOrDigit(c) || c == '_')) { ok = false; break; }
                if (ok) sink.Add(name);
                i = j + 1;
            } else i++;
        }
    }

    // Compute batch context for a scope. Tries in order:
    //   (a) Single qualified `%(X.M)` ref → batch over X.
    //   (b) Multiple qualified refs → unified single-batch fallback to first type
    //       (true cross-product batching isn't implemented; this matches the common case
    //       where the "extra" qualified type's item list is empty at runtime).
    //   (c) Only unqualified `%(M)` refs + a single @(Y) ref in the scope → batch over Y.
    //   (d) Only unqualified refs + a self-batch hint (the item type being defined) → batch over the hint.
    //   (e) Otherwise null — no batch wrapping needed (or no inference possible).
    static string? InferBatchType(
        IEnumerable<string?> metadataScopeExprs,
        IEnumerable<string?>? itemRefScopeExprs = null,
        string? selfBatchHint = null)
    {
        var qualified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasUnq = false;
        foreach (var e in metadataScopeExprs) {
            var (t, u) = ScanMetadataRefs(e);
            foreach (var x in t) qualified.Add(x);
            hasUnq |= u;
        }
        if (qualified.Count == 0 && !hasUnq) return null;
        if (qualified.Count >= 1) return qualified.First();
        // Only unqualified refs — need inference.
        if (itemRefScopeExprs != null) {
            var listRefs = ScanItemListRefs(itemRefScopeExprs);
            if (listRefs.Count == 1) return listRefs.First();
        }
        if (selfBatchHint != null) return selfBatchHint;
        return null; // give up; caller decides
    }

    static string? DetermineBatchItemType(IEnumerable<string?> exprs, string scopeName) {
        var batch = InferBatchType(exprs);
        if (batch == null) {
            // Distinguish "no metadata at all" from "unqualified-only that we can't infer".
            var (q, u) = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);
            foreach (var e in exprs) { var (t, hu) = ScanMetadataRefs(e); foreach (var x in t) q.Add(x); u |= hu; }
            if (q.Count == 0 && !u) return null;
            if (q.Count == 0 && u) throw new InvalidOperationException($"{scopeName}: unqualified %(Meta) refs without qualified context");
            if (q.Count > 1) throw new InvalidOperationException($"{scopeName}: multi-batch ({string.Join(",", q)}) not supported");
        }
        return batch;
    }

    static string? TryParseDirectItemRef(string expr) {
        var trimmed = expr.Trim();
        if (!trimmed.StartsWith("@(") || !trimmed.EndsWith(")")) return null;
        var inner = trimmed.Substring(2, trimmed.Length - 3).Trim();
        if (inner.Length == 0) return null;
        foreach (var c in inner) if (!char.IsLetterOrDigit(c) && c != '_') return null;
        return inner;
    }

    static (string ItemType, string ItemsExpr, string Selector)? TryParseItemProjection(string expr) {
        var trimmed = expr.Trim();
        if (!trimmed.StartsWith("@(") || !trimmed.EndsWith(")")) return null;
        var inner = trimmed.Substring(2, trimmed.Length - 3).Trim();
        var arrow = inner.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0) return null;
        var itemType = inner.Substring(0, arrow).Trim();
        if (itemType.Length == 0) return null;
        foreach (var c in itemType) if (!char.IsLetterOrDigit(c) && c != '_') return null;

        var itemsExpr = ItemAccess(itemType);
        var selector = "transformItem.Identity";
        var batchCtx = itemType.ToLowerInvariant();
        var pos = arrow + 2;
        while (pos < inner.Length) {
            while (pos < inner.Length && char.IsWhiteSpace(inner[pos])) pos++;
            if (pos >= inner.Length) break;
            if (inner[pos] == ',') {
                pos++;
                while (pos < inner.Length && char.IsWhiteSpace(inner[pos])) pos++;
                if (pos >= inner.Length || inner[pos] != '\'') return null;
                var close = inner.IndexOf('\'', pos + 1);
                if (close < 0) return null;
                if (inner.Substring(pos + 1, close - pos - 1) != ";") return null;
                pos = close + 1;
                break;
            }
            if (inner[pos] == '\'') {
                var close = inner.IndexOf('\'', pos + 1);
                if (close < 0) return null;
                var format = inner.Substring(pos + 1, close - pos - 1);
                var compiled = ExprCompiler.TryCompile(format, batchCtx);
                if (compiled == null) return null;
                selector = compiled.Replace("batchItem.", "transformItem.", StringComparison.Ordinal);
                pos = close + 1;
            } else {
                var nameStart = pos;
                while (pos < inner.Length && (char.IsLetterOrDigit(inner[pos]) || inner[pos] == '_')) pos++;
                if (pos == nameStart || pos >= inner.Length || inner[pos] != '(') return null;
                var fn = inner.Substring(nameStart, pos - nameStart);
                pos++;
                var depth = 1;
                var argsStart = pos;
                while (pos < inner.Length && depth > 0) {
                    if (inner[pos] == '(') depth++;
                    else if (inner[pos] == ')') depth--;
                    if (depth > 0) pos++;
                }
                if (pos >= inner.Length) return null;
                var args = ParseProjectionArgs(inner.Substring(argsStart, pos - argsStart));
                if (args == null) return null;
                pos++;
                switch (fn) {
                    case "WithMetadataValue" when args.Count == 2:
                        itemsExpr = $"{itemsExpr}.Where(transformItem => transformItem.HasMetadata({args[0]}, {args[1]}))";
                        break;
                    case "WithoutMetadataValue" when args.Count == 2:
                        itemsExpr = $"{itemsExpr}.Where(transformItem => !transformItem.HasMetadata({args[0]}, {args[1]}))";
                        break;
                    case "ClearMetadata" when args.Count == 0:
                        break;
                    case "Distinct" when args.Count == 0:
                        itemsExpr = $"{itemsExpr}.GroupBy(transformItem => {selector}, StringComparer.OrdinalIgnoreCase).Select(group => group.First())";
                        break;
                    case "Reverse" when args.Count == 0:
                        itemsExpr = $"{itemsExpr}.Reverse()";
                        break;
                    case "Trim" when args.Count == 0:
                        selector = $"({selector}).Trim()";
                        break;
                    case "TrimStart" when args.Count == 1:
                        selector = $"({selector}).TrimStart(({args[0]}).ToCharArray())";
                        break;
                    case "TrimEnd" when args.Count == 1:
                        selector = $"({selector}).TrimEnd(({args[0]}).ToCharArray())";
                        break;
                    case "ToUpper" when args.Count == 0:
                    case "ToUpperInvariant" when args.Count == 0:
                        selector = $"({selector}).ToUpperInvariant()";
                        break;
                    case "ToLower" when args.Count == 0:
                    case "ToLowerInvariant" when args.Count == 0:
                        selector = $"({selector}).ToLowerInvariant()";
                        break;
                    case "Replace" when args.Count == 2:
                        selector = $"({selector}).Replace({args[0]}, {args[1]})";
                        break;
                    default:
                        return null;
                }
            }
            while (pos < inner.Length && char.IsWhiteSpace(inner[pos])) pos++;
            if (pos + 1 < inner.Length && inner[pos] == '-' && inner[pos + 1] == '>') pos += 2;
            else if (pos < inner.Length && inner[pos] != ',') break;
        }
        return (itemType, itemsExpr, selector.Replace("transformItem.", "sourceItem.", StringComparison.Ordinal));
    }

    static (string ItemType, string MetadataName)? TryParseSimpleMetadataProjection(string expr) {
        var trimmed = expr.Trim();
        if (!trimmed.StartsWith("@(") || !trimmed.EndsWith(")")) return null;
        var inner = trimmed.Substring(2, trimmed.Length - 3).Trim();
        var arrow = inner.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0) return null;
        var itemType = inner.Substring(0, arrow).Trim();
        if (itemType.Length == 0) return null;
        foreach (var c in itemType) if (!char.IsLetterOrDigit(c) && c != '_') return null;
        var selector = inner.Substring(arrow + 2).Trim();
        if (selector.StartsWith("'") && selector.EndsWith("'"))
            selector = selector.Substring(1, selector.Length - 2).Trim();
        if (!selector.StartsWith("%(") || !selector.EndsWith(")")) return null;
        var metadata = selector.Substring(2, selector.Length - 3).Trim();
        if (metadata.Length == 0) return null;
        foreach (var c in metadata) if (!char.IsLetterOrDigit(c) && c != '_') return null;
        return (itemType, metadata);
    }

    static (string ItemType, string MetadataName)? TryParseDynamicItemInclude(string expr) {
        var trimmed = expr.Trim();
        if (!trimmed.StartsWith("@(") || !trimmed.EndsWith(")")) return null;
        var inner = trimmed.Substring(2, trimmed.Length - 3).Trim();
        if (!inner.StartsWith("%(") || !inner.EndsWith(")")) return null;
        var metaRef = inner.Substring(2, inner.Length - 3).Trim();
        var dot = metaRef.IndexOf('.');
        if (dot < 0) return null;
        var itemType = metaRef.Substring(0, dot).Trim();
        var metadata = metaRef.Substring(dot + 1).Trim();
        if (itemType.Length == 0 || metadata.Length == 0) return null;
        foreach (var c in itemType) if (!char.IsLetterOrDigit(c) && c != '_') return null;
        foreach (var c in metadata) if (!char.IsLetterOrDigit(c) && c != '_') return null;
        return (itemType, metadata);
    }

    static string? TryParsePropertySemicolonSplit(string expr) {
        var trimmed = expr.Trim();
        if (!trimmed.StartsWith("$(", StringComparison.Ordinal) || !trimmed.EndsWith(")", StringComparison.Ordinal)) return null;
        var inner = trimmed.Substring(2, trimmed.Length - 3).Trim();
        foreach (var suffix in new[] { ".Split(';')", ".Split(`;`)" }) {
            if (!inner.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var propertyName = inner.Substring(0, inner.Length - suffix.Length);
            if (propertyName.Length == 0) return null;
            foreach (var c in propertyName) {
                if (!char.IsLetterOrDigit(c) && c != '_') return null;
            }
            return ExprCompiler.EmitPropertyRead(propertyName);
        }
        return null;
    }

    static List<string>? ParseProjectionArgs(string block) {
        var raw = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        var inQuote = false;
        var quote = '\0';
        foreach (var c in block) {
            if (inQuote) {
                current.Append(c);
                if (c == quote) inQuote = false;
                continue;
            }
            if (c == '\'' || c == '`') { inQuote = true; quote = c; current.Append(c); continue; }
            if (c == '(' || c == '[') { depth++; current.Append(c); continue; }
            if (c == ')' || c == ']') { depth--; current.Append(c); continue; }
            if (c == ',' && depth == 0) { raw.Add(current.ToString().Trim()); current.Clear(); continue; }
            current.Append(c);
        }
        if (current.Length > 0) raw.Add(current.ToString().Trim());

        var compiled = new List<string>(raw.Count);
        foreach (var arg in raw) {
            string? value;
            if (arg.Length >= 2 && (arg[0] == '\'' && arg[^1] == '\'' || arg[0] == '`' && arg[^1] == '`'))
                value = ExprCompiler.TryCompile(arg.Substring(1, arg.Length - 2));
            else
                value = ExprCompiler.TryCompile(arg);
            if (value == null) return null;
            compiled.Add(value);
        }
        return compiled;
    }

    // Returns true if the expression is a pure literal (no MSBuild $/@/%-refs and no `;`),
    // meaning it represents exactly one item — we can emit a direct construction without
    // building a string and splitting it at runtime.
    static bool IsPureLiteralNoSemicolon(string expr) {
        if (string.IsNullOrEmpty(expr)) return false;
        for (int i = 0; i < expr.Length; i++) {
            char c = expr[i];
            if (c == ';') return false;
            if ((c == '$' || c == '@' || c == '%') && i + 1 < expr.Length && expr[i + 1] == '(') return false;
        }
        return true;
    }

    public static string CSharpLiteral(string? s) {
        if (s == null) return "null";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s) {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\t') sb.Append("\\t");
            else sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    static bool TryUnquoteStringLiteral(string expr, out string value) {
        value = "";
        if (expr.Length < 2 || expr[0] != '"' || expr[^1] != '"') return false;
        try {
            value = System.Text.Json.JsonSerializer.Deserialize<string>(expr) ?? "";
            return true;
        } catch {
            return false;
        }
    }

    static string SanitizeIdent(string s) {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    static string CSharpEscape(string s) {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\n') sb.Append("\\n");
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

// Compile MSBuild conditions into direct C# bool expressions.
static class CondCompiler {
    public static string? TryCompile(string cond, string? batchItemType = null) {
        var p = new Parser(cond, batchItemType);
        try {
            var r = p.ParseOr();
            p.SkipWs();
            return p.HasMore ? null : r;
        } catch {
            return null;
        }
    }

    class Parser {
        readonly string _src;
        readonly string? _batchItemType;
        int _i;
        public Parser(string src, string? batchItemType) { _src = src; _batchItemType = batchItemType; _i = 0; }
        public bool HasMore => _i < _src.Length;

        public string ParseOr() {
            var terms = new List<string> { ParseAnd() };
            while (true) {
                SkipWs();
                if (MatchKw("OR") || MatchKw("or") || MatchOp("||")) { terms.Add(ParseAnd()); } else break;
            }
            return SimplifyOrTerms(terms);
        }
        string ParseAnd() {
            var left = ParseNot();
            while (true) {
                SkipWs();
                if (MatchKw("AND") || MatchKw("and") || MatchOp("&&")) { left = $"({left} && {ParseNot()})"; } else break;
            }
            return left;
        }
        string ParseNot() {
            SkipWs();
            if (MatchOp("!")) return $"!({ParseNot()})";
            return ParseAtom();
        }
        string ParseAtom() {
            SkipWs();
            if (MatchOp("(")) {
                var v = ParseOr();
                SkipWs();
                if (!MatchOp(")")) throw new InvalidOperationException("expected )");
                return $"({v})";
            }
            var (left, leftIsBool) = ReadValueOrCall();
            SkipWs();
            if (TryReadCmp(out var op)) {
                if (leftIsBool) throw new InvalidOperationException("bool in comparison");
                SkipWs();
                var (right, rightIsBool) = ReadValueOrCall();
                if (rightIsBool) throw new InvalidOperationException("bool in comparison");
                if (op == "==" || op == "!=") {
                    string? c = null;
                    // Special case: `'@(X)' == ''` becomes `(I.X.Count == 0)` (no Join allocation).
                    string? itemVar = right == "\"\"" ? TryExtractItemList(left)
                                    : left == "\"\""  ? TryExtractItemList(right)
                                    : null;
                    if (itemVar != null) {
                        return op == "==" ? $"({itemVar}.Count == 0)" : $"({itemVar}.Count != 0)";
                    }
                    if (TryExtractBoolString(left, out var leftBool) && TryExtractBoolLiteral(right, out var rightBool)) {
                        var boolComparison = rightBool ? leftBool : $"!({leftBool})";
                        return op == "==" ? boolComparison : $"!({boolComparison})";
                    }
                    if (TryExtractBoolString(right, out var rightBoolExpr) && TryExtractBoolLiteral(left, out var leftBoolLiteral)) {
                        var boolComparison = leftBoolLiteral ? rightBoolExpr : $"!({rightBoolExpr})";
                        return op == "==" ? boolComparison : $"!({boolComparison})";
                    }
                    // Special case: `'%(X.Meta)' == 'literal'` becomes `batchItem.HasMetadata("meta", "literal")`.
                    // Avoids the lengthy string.Equals(GetMetadata(...)) construction. The receiver
                    // current metadata receiver is preserved.
                    string? meta = TryExtractMetadataCall(left, out var leftReceiver, out var leftMetaName)
                                       ? $"{leftReceiver}.HasMetadata({CSharpStringLiteral(leftMetaName!)}, {right})"
                                 : TryExtractMetadataCall(right, out var rightReceiver, out var rightMetaName)
                                       ? $"{rightReceiver}.HasMetadata({CSharpStringLiteral(rightMetaName!)}, {left})"
                                 : null;
                    if (meta != null) return op == "==" ? meta : "!" + meta;
                    if (right == "\"\"") c = $"string.IsNullOrEmpty({left})";
                    else if (left == "\"\"") c = $"string.IsNullOrEmpty({right})";
                    else c = $"string.Equals({left}, {right}, StringComparison.OrdinalIgnoreCase)";
                    return op == "==" ? c : $"!{c}";
                }
                return op switch {
                    ">"  => $"(CondHelpers.NumericCompare({left}, {right}) > 0)",
                    "<"  => $"(CondHelpers.NumericCompare({left}, {right}) < 0)",
                    ">=" => $"(CondHelpers.NumericCompare({left}, {right}) >= 0)",
                    "<=" => $"(CondHelpers.NumericCompare({left}, {right}) <= 0)",
                    _ => throw new InvalidOperationException(),
                };
            }
            if (leftIsBool) return left;
            return $"string.Equals({left}, \"true\", StringComparison.OrdinalIgnoreCase)";
        }

        (string expr, bool isBool) ReadValueOrCall() {
            SkipWs();
            int j = _i;
            while (j < _src.Length && (char.IsLetterOrDigit(_src[j]) || _src[j] == '.' || _src[j] == '_')) j++;
            if (j > _i && j < _src.Length && _src[j] == '(') {
                var name = _src.Substring(_i, j - _i);
                _i = j + 1;
                var args = new List<string>();
                SkipWs();
                if (!MatchOp(")")) {
                    while (true) {
                        SkipWs();
                        var (v, _) = ReadValueOrCall(); args.Add(v);
                        SkipWs();
                        if (MatchOp(",")) continue;
                        if (MatchOp(")")) break;
                        throw new InvalidOperationException("expected , or )");
                    }
                }
                return (CompileFn(name, args), true);
            }
            return (ReadValue(), false);
        }
        string ReadValue() {
            SkipWs();
            if (_i < _src.Length && _src[_i] == '\'') {
                _i++;
                int start = _i;
                // Scan to the matching closing quote, but skip past `$(...)`/`@(...)`/`%(...)`
                // groups so single quotes INSIDE them (e.g. `@(X->Method('a','b'))`) don't
                // prematurely terminate the outer quoted value.
                while (_i < _src.Length && _src[_i] != '\'') {
                    char ch = _src[_i];
                    if ((ch == '$' || ch == '@' || ch == '%') && _i + 1 < _src.Length && _src[_i + 1] == '(') {
                        _i += 2;
                        int depth = 1;
                        while (_i < _src.Length && depth > 0) {
                            char d = _src[_i];
                            if (d == '(') depth++;
                            else if (d == ')') depth--;
                            _i++;
                        }
                        continue;
                    }
                    _i++;
                }
                string raw = _src.Substring(start, _i - start);
                if (_i < _src.Length) _i++;
                var c = ExprCompiler.TryCompile(raw, _batchItemType);
                if (c == null) throw new InvalidOperationException("uncompilable expr in value");
                return c;
            }
            int s = _i;
            while (_i < _src.Length) {
                char c = _src[_i];
                // Unquoted MSBuild references: $(prop), @(item), %(meta), $([fn]::call(...)).
                // Each starts with one of $/@/% followed by ( and is balanced — read through
                // the matching close paren, including nested parens. Without this we'd break
                // at the first `(` and ExprCompiler would only see the sigil character.
                if ((c == '$' || c == '@' || c == '%') && _i + 1 < _src.Length && _src[_i + 1] == '(') {
                    _i += 2;
                    int depth = 1;
                    while (_i < _src.Length && depth > 0) {
                        char d = _src[_i];
                        if (d == '(') depth++;
                        else if (d == ')') depth--;
                        _i++;
                    }
                    continue;
                }
                if (char.IsWhiteSpace(c) || c == ')' || c == '(' || c == ',') break;
                if (IsOpStart(c)) break;
                _i++;
            }
            var raw2 = _src.Substring(s, _i - s);
            if (raw2.Length == 0) return "\"\"";   // bare op (e.g., ' != X'): treat missing side as ""
            var compiled = ExprCompiler.TryCompile(raw2, _batchItemType);
            if (compiled == null) throw new InvalidOperationException("uncompilable expr in value");
            return compiled;
        }
        string CompileFn(string name, List<string> args) {
            // MSBuild condition functions are case-insensitive ("Exists" vs "exists").
            return name.ToLowerInvariant() switch {
                "exists" => $"(File.Exists({args[0]}) || Directory.Exists({args[0]}))",
                "hastrailingslash" => $"({args[0]}.Length > 0 && ({args[0]}.EndsWith('/') || {args[0]}.EndsWith('\\\\')))",
                _ => throw new InvalidOperationException($"fn not compiled: {name}"),
            };
        }
        public void SkipWs() { while (_i < _src.Length && char.IsWhiteSpace(_src[_i])) _i++; }
        bool MatchKw(string kw) {
            SkipWs();
            if (_i + kw.Length > _src.Length) return false;
            // MSBuild keywords (`and`/`or`/`AND`/`Or`/etc.) are case-insensitive in conditions.
            if (string.Compare(_src, _i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            int after = _i + kw.Length;
            if (after < _src.Length && (char.IsLetterOrDigit(_src[after]) || _src[after] == '_')) return false;
            _i = after; return true;
        }
        bool MatchOp(string op) {
            SkipWs();
            if (_i + op.Length > _src.Length) return false;
            if (string.Compare(_src, _i, op, 0, op.Length, StringComparison.Ordinal) != 0) return false;
            _i += op.Length; return true;
        }
        bool TryReadCmp(out string op) {
            SkipWs();
            foreach (var c in new[] { "==", "!=", "<=", ">=", "<", ">" }) if (MatchOp(c)) { op = c; return true; }
            op = ""; return false;
        }
        static bool IsOpStart(char c) => c is '!' or '=' or '<' or '>' or '&' or '|';

        static string SimplifyOrTerms(List<string> terms) {
            if (terms.Count == 1) return terms[0];

            var result = new List<string>();
            var byExpression = new Dictionary<string, List<(int Index, string Literal)>>(StringComparer.Ordinal);
            for (var i = 0; i < terms.Count; i++) {
                var term = terms[i];
                if (TryExtractStringEquals(term, out var boolStringExpr, out var boolLiteral)
                    && TryExtractBoolString(boolStringExpr, out var boolExpr)
                    && TryExtractBoolLiteral(boolLiteral, out var boolValue))
                {
                    term = boolValue ? boolExpr : $"!({boolExpr})";
                }
                result.Add(term);
                if (TryExtractStringEquals(term, out var expr, out var literal)) {
                    if (!byExpression.TryGetValue(expr, out var matches)) {
                        matches = new List<(int Index, string Literal)>();
                        byExpression.Add(expr, matches);
                    }
                    matches.Add((i, literal));
                }
            }

            foreach (var (expr, matches) in byExpression) {
                if (matches.Count < 2) continue;
                var literals = string.Join(", ", matches.Select(match => match.Literal));
                result[matches[0].Index] = $"CondHelpers.IsAny({expr}, {literals})";
                for (var i = 1; i < matches.Count; i++)
                    result[matches[i].Index] = "";
            }

            var folded = result.Where(term => term.Length != 0).ToArray();
            return folded.Length == 1 ? folded[0] : $"({string.Join(" || ", folded)})";
        }

        static bool TryExtractStringEquals(string term, out string expr, out string literal) {
            expr = ""; literal = "";
            term = StripOuterParens(term.Trim());
            const string prefix = "string.Equals(";
            const string suffix = ", StringComparison.OrdinalIgnoreCase)";
            if (!term.StartsWith(prefix, StringComparison.Ordinal) || !term.EndsWith(suffix, StringComparison.Ordinal))
                return false;

            var args = term.Substring(prefix.Length, term.Length - prefix.Length - suffix.Length);
            var comma = FindTopLevelComma(args);
            if (comma < 0) return false;

            var left = args.Substring(0, comma).Trim();
            var right = args.Substring(comma + 1).Trim();
            if (IsStringLiteral(left) && !IsStringLiteral(right)) {
                expr = right;
                literal = left;
                return true;
            }
            if (IsStringLiteral(right) && !IsStringLiteral(left)) {
                expr = left;
                literal = right;
                return true;
            }
            return false;
        }

        static string StripOuterParens(string value) {
            while (value.Length >= 2 && value[0] == '(' && value[^1] == ')' && EnclosesWholeExpression(value))
                value = value.Substring(1, value.Length - 2).Trim();
            return value;
        }

        static bool EnclosesWholeExpression(string value) {
            var depth = 0;
            var inString = false;
            for (var i = 0; i < value.Length; i++) {
                var c = value[i];
                if (inString) {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '(') depth++;
                else if (c == ')') {
                    depth--;
                    if (depth == 0 && i != value.Length - 1)
                        return false;
                }
            }
            return depth == 0;
        }

        static int FindTopLevelComma(string value) {
            var depth = 0;
            var inString = false;
            for (var i = 0; i < value.Length; i++) {
                var c = value[i];
                if (inString) {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0) return i;
            }
            return -1;
        }

        static bool IsStringLiteral(string value) =>
            value.Length >= 2 && value[0] == '"' && value[^1] == '"';

        static bool TryExtractBoolLiteral(string value, out bool result) {
            value = value.Trim();
            if (string.Equals(value, "\"true\"", StringComparison.Ordinal)) {
                result = true;
                return true;
            }
            if (string.Equals(value, "\"false\"", StringComparison.Ordinal)) {
                result = false;
                return true;
            }
            result = false;
            return false;
        }

        static bool TryExtractBoolString(string value, out string boolExpr) {
            boolExpr = "";
            value = StripOuterParens(value.Trim());
            var question = FindTopLevelChar(value, '?');
            if (question < 0) return false;
            var colon = FindTopLevelChar(value.Substring(question + 1), ':');
            if (colon < 0) return false;
            colon += question + 1;

            var condition = value.Substring(0, question).Trim();
            var trueArm = value.Substring(question + 1, colon - question - 1).Trim();
            var falseArm = value.Substring(colon + 1).Trim();
            if (TryExtractBoolLiteral(trueArm, out var trueValue) && TryExtractBoolLiteral(falseArm, out var falseValue)) {
                if (trueValue && !falseValue) {
                    boolExpr = condition;
                    return true;
                }
                if (!trueValue && falseValue) {
                    boolExpr = $"!({condition})";
                    return true;
                }
            }
            return false;
        }

        static int FindTopLevelChar(string value, char needle) {
            var depth = 0;
            var inString = false;
            for (var i = 0; i < value.Length; i++) {
                var c = value[i];
                if (inString) {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == needle && depth == 0) return i;
            }
            return -1;
        }

        // Recognize the canonical compiled shape of an @(X) item-list reference,
        // i.e. ExprCompiler's output for a bare @(X). When we see this on one side
        // of an empty-string comparison, we can swap the allocating Join/IsNullOrEmpty
        // check for a direct .Count == 0 test.
        static string? TryExtractItemList(string expr) {
            const string prefix = "string.Join(\";\", I.";
            const string suffix = ".Select(transformItem => transformItem.Identity))";
            if (expr.Length > prefix.Length + suffix.Length
                && expr.StartsWith(prefix, StringComparison.Ordinal)
                && expr.EndsWith(suffix, StringComparison.Ordinal)) {
                var name = expr.Substring(prefix.Length, expr.Length - prefix.Length - suffix.Length);
                foreach (var c in name) if (!(char.IsLetterOrDigit(c) || c == '_')) return null;
                if (name.Length == 0) return null;
                return "I." + name;
            }
            return null;
        }

        // Recognizes the canonical compiled shape of a `%(X.Meta)` reference — for example
        // `batchItem.GetMetadata("meta")`. Returns the
        // receiver and the metadata name so the caller can substitute with HasMetadata.
        static bool TryExtractMetadataCall(string expr, out string? receiver, out string? metaName) {
            receiver = null; metaName = null;
            // Expr must end with `.GetMetadata("<name>")`. Find the call suffix.
            const string mid = ".GetMetadata(\"";
            int p = expr.LastIndexOf(mid, StringComparison.Ordinal);
            if (p <= 0 || !expr.EndsWith("\")", StringComparison.Ordinal)) return false;
            var recv = expr.Substring(0, p);
            // Receiver must be a simple identifier.
            // Avoid matching things like (a + b).GetMetadata(...).
            foreach (var c in recv) if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            if (recv.Length == 0) return false;
            var name = expr.Substring(p + mid.Length, expr.Length - p - mid.Length - 2);
            // Reject names that contain quote characters (shouldn't happen for our literals).
            if (name.Contains('"') || name.Contains('\\')) return false;
            receiver = recv;
            metaName = name;
            return true;
        }

        // Quote a C# string literal. Used by the HasMetadata substitution to encode the
        // metadata-name argument; the input has been validated as letter/digit/underscore.
        static string CSharpStringLiteral(string s) => "\"" + s + "\"";
    }
}

// Compile MSBuild expression strings into C# string expressions.
static class ExprCompiler {
    public static string? TryCompile(string expr, string? batchItemType = null) {
        if (string.IsNullOrEmpty(expr)) return "\"\"";
        // Normalize multi-line MSBuild expression whitespace.
        // XML element bodies like  <Inputs>$(A);\n    $(B);\n    @(C)</Inputs>
        // get parsed verbatim by MSBuild; the whitespace between list elements is
        // preserved in the raw string and only trimmed when MSBuild splits on `;`.
        // Strip "newline + following whitespace" so generated literals are clean.
        expr = StripMultilineWhitespace(expr);
        var parts = new List<string>();
        var lit = new System.Text.StringBuilder();
        int i = 0;
        while (i < expr.Length) {
            char c = expr[i];
            if (c == '$' && i + 1 < expr.Length && expr[i + 1] == '(') {
                int j = FindMatching(expr, i + 1);
                if (j < 0) return null;
                var inner = expr.Substring(i + 2, j - i - 2);
                if (inner.StartsWith("[")) {
                    var pf = PropertyFnCompiler.TryCompile(inner, batchItemType);
                    if (pf == null) return null;
                    if (lit.Length > 0) { parts.Add(LitToCSharp(lit.ToString())); lit.Clear(); }
                    parts.Add(pf);
                    i = j + 1;
                    continue;
                }
                if (inner.StartsWith("Registry:", StringComparison.OrdinalIgnoreCase)) return null;
                // Dynamic property name from metadata: `$(%(X.Y))` — the inner `%(X.Y)`
                // resolves to a string at runtime which is then used as the property name.
                if (inner.StartsWith("%(") && inner.EndsWith(")")) {
                    var metaCompiled = TryCompile(inner, batchItemType);
                    if (metaCompiled == null) return null;
                    if (lit.Length > 0) { parts.Add(LitToCSharp(lit.ToString())); lit.Clear(); }
                    parts.Add($"P.Get({metaCompiled})");
                    i = j + 1;
                    continue;
                }
                // Plain property ref or property instance-method chain.
                // Plain ref: `$(Foo)` — no `$/@/%/(` in the inner.
                // Chain:     `$(Foo.Method(args).Method(args)...)` — first `.` at depth 0
                //            kicks off the chain parser. We split the inner at the first
                //            unparen-nested `.`, treat the left as a property name, and
                //            apply the right as an instance-method chain on its value.
                int dotAt = FindTopLevelDot(inner);
                if (dotAt > 0 && IsPlainIdent(inner.AsSpan(0, dotAt))) {
                    var chain = InstanceMethodChainCompiler.TryCompile(
                        receiver: EmitPropertyRead(inner.Substring(0, dotAt)),
                        chain: inner.Substring(dotAt));
                    if (chain == null) return null;
                    if (lit.Length > 0) { parts.Add(LitToCSharp(lit.ToString())); lit.Clear(); }
                    parts.Add(chain);
                    i = j + 1;
                    continue;
                }
                if (inner.Contains("$") || inner.Contains("(")) return null;
                if (lit.Length > 0) { parts.Add(LitToCSharp(lit.ToString())); lit.Clear(); }
                parts.Add(EmitPropertyRead(inner));
                i = j + 1;
            } else if (c == '@' && i + 1 < expr.Length && expr[i + 1] == '(') {
                int j = FindMatching(expr, i + 1);
                if (j < 0) return null;
                var inner = expr.Substring(i + 2, j - i - 2);
                var compiledItemRef = CompileItemRef(inner);
                if (compiledItemRef == null) return null;
                if (lit.Length > 0) { parts.Add(LitToCSharp(lit.ToString())); lit.Clear(); }
                parts.Add(compiledItemRef);
                i = j + 1;
            } else if (c == '%' && i + 1 < expr.Length && expr[i + 1] == '(') {
                int j = expr.IndexOf(')', i + 2);
                if (j < 0) return null;
                var inner = expr.Substring(i + 2, j - i - 2);
                string metaName;
                int dot = inner.IndexOf('.');
                if (dot >= 0) {
                    var refIt = inner.Substring(0, dot);
                    metaName = inner.Substring(dot + 1);
                    // Qualified ref `%(X.Meta)`.
                    if (batchItemType != null && string.Equals(refIt, batchItemType, StringComparison.OrdinalIgnoreCase)) {
                        // In the matching batch context — use the current item.
                        if (lit.Length > 0) { parts.Add(LitToCSharp(lit.ToString())); lit.Clear(); }
                        parts.Add($"batchItem.GetMetadata(\"{metaName.ToLowerInvariant()}\")");
                        i = j + 1;
                        continue;
                    }
                    // Out-of-batch qualified ref — iterate all items of refIt and join their metadata.
                    if (lit.Length > 0) { parts.Add(LitToCSharp(lit.ToString())); lit.Clear(); }
                    parts.Add($"string.Join(\";\", {Emitter.ItemAccess(refIt)}.Select(item => item.GetMetadata(\"{metaName.ToLowerInvariant()}\")))");
                    i = j + 1;
                    continue;
                }
                metaName = inner;
                if (lit.Length > 0) { parts.Add(LitToCSharp(lit.ToString())); lit.Clear(); }
                // Unqualified `%(M)` without a batch context: MSBuild semantics fall through
                // to "missing metadata is empty string". Codegen-time we don't know which item
                // this should batch against, so emit "" as a best-effort substitution. (Most
                // SDK targets that hit this path are running in a batch we can't statically
                // see, like satellite-resource generation.)
                if (batchItemType == null) { parts.Add("\"\""); i = j + 1; continue; }
                parts.Add($"batchItem.GetMetadata(\"{metaName.ToLowerInvariant()}\")");
                i = j + 1;
            } else {
                lit.Append(c);
                i++;
            }
        }
        if (lit.Length > 0) parts.Add(LitToCSharp(lit.ToString()));
        if (parts.Count == 0) return "\"\"";
        if (parts.Count == 1) return parts[0];
        return $"({string.Join(" + ", parts)})";
    }

    public static string EmitPropertyRead(string name) {
        var lower = name.ToLowerInvariant();
        return Emitter.TryCanonicalProp(lower, out var canon) ? $"P.{canon}" : $"P.GetExtra(\"{lower}\")";
    }

    static string? CompileItemRef(string inner) {
        inner = inner.Trim();

        // Special case: @(%(X.Identity)) — dynamic item ref using a metadata value
        // as the item type name. Rare but present in SDK.
        if (inner.StartsWith("%(") && inner.EndsWith(")") && inner.IndexOf("->") < 0) {
            var metaRef = inner.Substring(2, inner.Length - 3);
            int dot = metaRef.IndexOf('.');
            if (dot < 0) return null;
            var sourceItemType = metaRef.Substring(0, dot);
            var metaName = metaRef.Substring(dot + 1);
            if (!string.Equals(metaName, "Identity", StringComparison.OrdinalIgnoreCase)) return null;
            return $"string.Join(\";\", {Emitter.ItemAccess(sourceItemType)}.SelectMany(sourceItem => I.Get(sourceItem.Identity)).Select(transformItem => transformItem.Identity))";
        }

        // Parse: ItemName ( -> op )*  followed by an optional , 'separator'.
        // Each op is either a quoted projection 'fmt' (sets the per-item selector) or a
        // function-call Foo(args) (filter/aggregate). Filters preserve the collection;
        // aggregates terminate the chain by returning a scalar string.
        int arrow = inner.IndexOf("->");
        string itemName = (arrow >= 0 ? inner.Substring(0, arrow) : SplitOffSeparator(inner, out _)).Trim();
        if (itemName.Length == 0) return null;
        foreach (var c in itemName) if (!char.IsLetterOrDigit(c) && c != '_') return null;

        var batchCtx = itemName.ToLowerInvariant();
        string itemsExpr = Emitter.ItemAccess(itemName);       // IEnumerable<Item>
        string selector = "transformItem.Identity";
        string separator = ";";
        string? scalarResult = null;                            // when an aggregate has been applied

        int pos = arrow >= 0 ? arrow + 2 : inner.Length;
        while (pos < inner.Length && scalarResult == null) {
            while (pos < inner.Length && char.IsWhiteSpace(inner[pos])) pos++;
            if (pos >= inner.Length) break;
            if (inner[pos] == ',') {
                pos++;
                while (pos < inner.Length && char.IsWhiteSpace(inner[pos])) pos++;
                if (pos >= inner.Length || inner[pos] != '\'') return null;
                int close = inner.IndexOf('\'', pos + 1);
                if (close < 0) return null;
                separator = inner.Substring(pos + 1, close - pos - 1);
                pos = close + 1;
                break; // separator must be last
            }
            if (inner[pos] == '\'') {
                int close = inner.IndexOf('\'', pos + 1);
                if (close < 0) return null;
                var fmt = inner.Substring(pos + 1, close - pos - 1);
                var compiledFmt = TryCompile(fmt, batchCtx);
                if (compiledFmt == null) return null;
                selector = compiledFmt.Replace("batchItem.", "transformItem.", StringComparison.Ordinal);
                pos = close + 1;
            } else {
                // Function call: Name(args).
                int nameStart = pos;
                while (pos < inner.Length && (char.IsLetterOrDigit(inner[pos]) || inner[pos] == '_')) pos++;
                if (pos == nameStart || pos >= inner.Length || inner[pos] != '(') return null;
                var fn = inner.Substring(nameStart, pos - nameStart);
                pos++;
                int depth = 1; int argsStart = pos;
                while (pos < inner.Length && depth > 0) {
                    if (inner[pos] == '(') depth++;
                    else if (inner[pos] == ')') depth--;
                    if (depth > 0) pos++;
                }
                if (pos >= inner.Length) return null;
                var argsBlock = inner.Substring(argsStart, pos - argsStart);
                pos++; // skip )
                var args = ParseQuotedArgs(argsBlock);
                if (args == null) return null;
                var (newItems, newSelector, scalar) = ApplyItemOp(itemsExpr, selector, fn, args);
                if (newItems == null && scalar == null) return null;
                if (scalar != null) scalarResult = scalar;
                else { itemsExpr = newItems!; selector = newSelector!; }
            }
            // Optional `->` continuation
            while (pos < inner.Length && char.IsWhiteSpace(inner[pos])) pos++;
            if (pos + 1 < inner.Length && inner[pos] == '-' && inner[pos + 1] == '>') { pos += 2; }
            else if (pos < inner.Length && inner[pos] != ',') break;
        }

        if (scalarResult != null) return scalarResult;
        return $"string.Join({LitToCSharp(separator)}, {itemsExpr}.Select(transformItem => {selector}))";
    }

    // Split off a trailing `, 'sep'` from an item ref's body (no arrow form).
    static string SplitOffSeparator(string s, out string? sep) {
        sep = null;
        int comma = FindTopLevelComma(s);
        if (comma < 0) return s;
        return s.Substring(0, comma);
    }

    // Parse a function-call argument block. Args are top-level-comma-separated; each is
    // either 'literal' / `literal` (compile inner via ExprCompiler) or a bare expression.
    static List<string>? ParseQuotedArgs(string block) {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(block)) return result;
        int depth = 0; bool inQuote = false; char qc = '\0';
        var cur = new System.Text.StringBuilder();
        foreach (var c in block) {
            if (inQuote) { cur.Append(c); if (c == qc) inQuote = false; continue; }
            if (c == '\'' || c == '`') { inQuote = true; qc = c; cur.Append(c); continue; }
            if (c == '(' || c == '[') { depth++; cur.Append(c); continue; }
            if (c == ')' || c == ']') { depth--; cur.Append(c); continue; }
            if (c == ',' && depth == 0) { result.Add(cur.ToString().Trim()); cur.Clear(); continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) result.Add(cur.ToString().Trim());
        // Compile each arg through ExprCompiler.
        var compiled = new List<string>(result.Count);
        foreach (var a in result) {
            string? c;
            if (a.Length >= 2 && (a[0] == '\'' && a[^1] == '\'' || a[0] == '`' && a[^1] == '`'))
                c = TryCompile(a.Substring(1, a.Length - 2));
            else
                c = TryCompile(a);
            if (c == null) return null;
            compiled.Add(c);
        }
        return compiled;
    }

    // Apply an item-op to the running (items, selector) state. Returns:
    //   - (newItems, newSelector, null) for non-terminating operations (filters)
    //   - (null, null, scalarStringExpr) for aggregates that produce a scalar
    //   - (null, null, null) if the op isn't supported
    static (string? items, string? selector, string? scalar) ApplyItemOp(string items, string selector, string fn, List<string> args) {
        switch (fn) {
            case "Count" when args.Count == 0:
                return (null, null, $"({items}.Count().ToString())");
            case "WithMetadataValue" when args.Count == 2:
                return ($"{items}.Where(transformItem => transformItem.HasMetadata({args[0]}, {args[1]}))", selector, null);
            case "WithoutMetadataValue" when args.Count == 2:
                return ($"{items}.Where(transformItem => !transformItem.HasMetadata({args[0]}, {args[1]}))", selector, null);
            case "AnyHaveMetadataValue" when args.Count == 2:
                return (null, null, $"({items}.Any(transformItem => transformItem.HasMetadata({args[0]}, {args[1]})) ? \"true\" : \"false\")");
            case "ClearMetadata" when args.Count == 0:
                return (items, selector, null);
            case "Distinct" when args.Count == 0:
                // Dedupe by current selector value, NOT by item reference, since the selector
                // determines what the join will produce. Keeps the running items collection of
                // matching items (with the FIRST item per distinct selector value).
                return ($"{items}.GroupBy(transformItem => {selector}, StringComparer.OrdinalIgnoreCase).Select(group => group.First())", selector, null);
            case "Reverse" when args.Count == 0:
                return ($"{items}.Reverse()", selector, null);
            // Per-item string methods applied to the current selector. These transform
            // the selector expression, not the underlying items.
            case "TrimStart" when args.Count == 1:
                return (items, $"({selector}).TrimStart({args[0]}.ToCharArray())", null);
            case "TrimEnd" when args.Count == 1:
                return (items, $"({selector}).TrimEnd({args[0]}.ToCharArray())", null);
            case "ToUpperInvariant" when args.Count == 0:
                return (items, $"({selector}).ToUpperInvariant()", null);
            case "ToLowerInvariant" when args.Count == 0:
                return (items, $"({selector}).ToLowerInvariant()", null);
            case "ToUpper" when args.Count == 0:
                return (items, $"({selector}).ToUpperInvariant()", null);
            case "ToLower" when args.Count == 0:
                return (items, $"({selector}).ToLowerInvariant()", null);
            case "Replace" when args.Count == 2:
                return (items, $"({selector}).Replace({args[0]}, {args[1]})", null);
            case "Substring" when args.Count == 1:
                return (items, $"({selector}).Substring(int.Parse({args[0]}))", null);
            case "Substring" when args.Count == 2:
                return (items, $"({selector}).Substring(int.Parse({args[0]}), int.Parse({args[1]}))", null);
            case "Trim" when args.Count == 0:
                return (items, $"({selector}).Trim()", null);
        }
        return (null, null, null);
    }

    static int FindClosingQuote(string s, int from) {
        for (int i = from; i < s.Length; i++) if (s[i] == '\'') return i;
        return -1;
    }
    static int FindTopLevelComma(string s) {
        int depth = 0; bool inQuote = false;
        for (int i = 0; i < s.Length; i++) {
            char c = s[i];
            if (c == '\'') inQuote = !inQuote;
            else if (!inQuote) {
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0) return i;
            }
        }
        return -1;
    }
    static int FindMatching(string s, int openParen) {
        int depth = 0;
        for (int i = openParen; i < s.Length; i++) {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    // Returns the index of the first `.` not inside any (..) group, or -1 if none.
    // Used to split `Foo.Method(args).Method(args)` between the property name and chain.
    static int FindTopLevelDot(string s) {
        int depth = 0;
        for (int i = 0; i < s.Length; i++) {
            char c = s[i];
            if (c == '(' || c == '[') depth++;
            else if (c == ')' || c == ']') depth--;
            else if (c == '.' && depth == 0) return i;
        }
        return -1;
    }

    static bool IsPlainIdent(ReadOnlySpan<char> s) {
        if (s.Length == 0) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        for (int i = 1; i < s.Length; i++) if (!char.IsLetterOrDigit(s[i]) && s[i] != '_') return false;
        return true;
    }
    public static string LitToCSharp(string s) {
        var sb = new System.Text.StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s) {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\t') sb.Append("\\t");
            else sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    static string StripMultilineWhitespace(string expr) {
        if (expr.IndexOf('\n') < 0 && expr.IndexOf('\r') < 0) return expr;
        var sb = new System.Text.StringBuilder(expr.Length);
        int i = 0;
        while (i < expr.Length) {
            char c = expr[i];
            if (c == '\r' || c == '\n') {
                // Drop the newline and all whitespace that immediately follows
                // (the next list-element's indentation).
                i++;
                while (i < expr.Length && (expr[i] == ' ' || expr[i] == '\t' || expr[i] == '\r' || expr[i] == '\n')) i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}

// Compile method-chain and member-access tails on a non-string receiver (used after
// a static property fn returns something other than a plain string — e.g. DateTime,
// Version, char). Supports `.PropertyName`, `.MethodName(args)`, `[intIndex]`.
static class ChainCompiler {
    public static string? Apply(string receiver, string chain) {
        chain = chain ?? "";
        var current = receiver;
        int i = 0;
        while (i < chain.Length) {
            if (chain[i] == '.') {
                i++;
                int start = i;
                while (i < chain.Length && (char.IsLetterOrDigit(chain[i]) || chain[i] == '_')) i++;
                if (i == start) return null;
                var name = chain.Substring(start, i - start);
                // Member followed by `(` → method call; otherwise property access.
                if (i < chain.Length && chain[i] == '(') {
                    i++;
                    int depth = 1, argsStart = i;
                    while (i < chain.Length && depth > 0) {
                        if (chain[i] == '(') depth++;
                        else if (chain[i] == ')') depth--;
                        if (depth > 0) i++;
                    }
                    if (depth != 0) return null;
                    var argsBlock = chain.Substring(argsStart, i - argsStart);
                    i++; // skip )
                    var args = SplitArgs(argsBlock);
                    if (args == null) return null;
                    current = ApplyCall(current, name, args);
                } else {
                    // Property access — pass through as `(current).Name`. The MSBuild value
                    // model is "anything stringifies to a string"; we rely on .ToString()
                    // happening implicitly where needed.
                    current = $"({current}).{name}";
                }
            } else if (chain[i] == '[') {
                int close = chain.IndexOf(']', i + 1);
                if (close < 0) return null;
                var idx = chain.Substring(i + 1, close - i - 1).Trim();
                if (!int.TryParse(idx, out var n)) return null;
                current = $"({current})[{n}]";
                i = close + 1;
            } else if (char.IsWhiteSpace(chain[i])) {
                i++;
            } else {
                return null;
            }
            if (current == null) return null;
        }
        // Ensure the final value is a string for MSBuild semantics.
        return $"({current}).ToString()";
    }

    static string? ApplyCall(string receiver, string method, List<string> args) {
        string R() => "(" + receiver + ")";
        return (method, args.Count) switch {
            ("ToString", 0) => $"{R()}.ToString()",
            ("ToString", 1) => $"{R()}.ToString({args[0]})",
            ("Replace",  2) => $"{R()}.Replace({args[0]}, {args[1]})",
            ("Substring",1) => $"{R()}.Substring(int.Parse({args[0]}))",
            ("Substring",2) => $"{R()}.Substring(int.Parse({args[0]}), int.Parse({args[1]}))",
            ("Split",    1) => $"{R()}.ToString().Split({args[0]}.ToCharArray(), StringSplitOptions.None)",
            ("ToLower",  0) or ("ToLowerInvariant", 0) => $"{R()}.ToString().ToLowerInvariant()",
            ("ToUpper",  0) or ("ToUpperInvariant", 0) => $"{R()}.ToString().ToUpperInvariant()",
            ("Trim",     0) => $"{R()}.ToString().Trim()",
            ("Contains", 1) => $"({R()}.ToString().Contains({args[0]}) ? \"true\" : \"false\")",
            ("StartsWith", 1) => $"({R()}.ToString().StartsWith({args[0]}) ? \"true\" : \"false\")",
            ("EndsWith", 1) => $"({R()}.ToString().EndsWith({args[0]}) ? \"true\" : \"false\")",
            _ => null,
        };
    }

    static List<string>? SplitArgs(string block) {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(block)) return result;
        int depth = 0; bool inQuote = false; char qc = '\0';
        var cur = new System.Text.StringBuilder();
        foreach (var c in block) {
            if (inQuote) { cur.Append(c); if (c == qc) inQuote = false; continue; }
            if (c == '\'' || c == '`') { inQuote = true; qc = c; cur.Append(c); continue; }
            if (c == '(' || c == '[') { depth++; cur.Append(c); continue; }
            if (c == ')' || c == ']') { depth--; cur.Append(c); continue; }
            if (c == ',' && depth == 0) { result.Add(cur.ToString().Trim()); cur.Clear(); continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) result.Add(cur.ToString().Trim());
        var compiled = new List<string>(result.Count);
        foreach (var a in result) {
            string? c;
            if (a.Length >= 2 && (a[0] == '\'' && a[^1] == '\'' || a[0] == '`' && a[^1] == '`'))
                c = ExprCompiler.TryCompile(a.Substring(1, a.Length - 2));
            else
                c = ExprCompiler.TryCompile(a);
            if (c == null) return null;
            compiled.Add(c);
        }
        return compiled;
    }
}

// Compiles a chain of instance-method calls applied to a string-typed C# expression.
// Handles patterns like `.Contains('x')`, `.Substring(0, 10).Trim()`, `.Replace('.', '_').ToUpperInvariant()`.
// Receiver is whatever the chain starts on (a property read, item Identity, etc.); chain
// starts with `.` and is one or more `.Name(args)` segments. Returns the final compiled
// C# expression, or null if any method/arity is unsupported.
static class InstanceMethodChainCompiler {
    public static string? TryCompile(string receiver, string chain) {
        var current = receiver;
        // Track when the last step left `current` typed as string[] (only Split today)
        // so an immediate `[i]` consumes the array form and any other consumer joins it.
        bool arrayPending = false;
        int i = 0;
        while (i < chain.Length) {
            if (chain[i] == '[') {
                int close = chain.IndexOf(']', i + 1);
                if (close < 0) return null;
                var idx = chain.Substring(i + 1, close - i - 1).Trim();
                if (!int.TryParse(idx, out var n)) return null;
                current = $"({current})[{n}]";
                arrayPending = false;
                i = close + 1;
                continue;
            }
            if (chain[i] != '.') return null;
            if (arrayPending) {
                // Method call on the Split result — collapse the array back to a `;`-joined
                // string before continuing so e.g. `.Split('-').ToUpper()` is well-typed.
                current = $"string.Join(\";\", {current})";
                arrayPending = false;
            }
            i++;
            int nameStart = i;
            while (i < chain.Length && (char.IsLetterOrDigit(chain[i]) || chain[i] == '_')) i++;
            if (i == nameStart) return null;
            var method = chain.Substring(nameStart, i - nameStart);
            if (i >= chain.Length || chain[i] != '(') return null;
            i++; // skip (
            int depth = 1, argsStart = i;
            while (i < chain.Length && depth > 0) {
                if (chain[i] == '(') depth++;
                else if (chain[i] == ')') depth--;
                if (depth > 0) i++;
            }
            if (depth != 0) return null;
            var argsBlock = chain.Substring(argsStart, i - argsStart);
            i++; // skip )
            var args = SplitArgs(argsBlock);
            if (args == null) return null;
            var applied = ApplyMethod(current, method, args);
            if (applied == null) return null;
            current = applied;
            arrayPending = method == "Split";
        }
        if (arrayPending) current = $"string.Join(\";\", {current})";
        return current;
    }

    static List<string>? SplitArgs(string argsBlock) {
        var raw = new List<string>();
        if (string.IsNullOrWhiteSpace(argsBlock)) return raw;
        int depth = 0; bool inQuote = false; char quoteCh = '\0';
        var cur = new System.Text.StringBuilder();
        foreach (var c in argsBlock) {
            if (inQuote) {
                cur.Append(c);
                if (c == quoteCh) inQuote = false;
                continue;
            }
            if (c == '\'' || c == '`') { inQuote = true; quoteCh = c; cur.Append(c); continue; }
            if (c == '(' || c == '[') { depth++; cur.Append(c); continue; }
            if (c == ')' || c == ']') { depth--; cur.Append(c); continue; }
            if (c == ',' && depth == 0) { raw.Add(cur.ToString().Trim()); cur.Clear(); continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) raw.Add(cur.ToString().Trim());

        // Compile each arg via ExprCompiler so callers can drop them straight into emitted C#.
        var compiled = new List<string>(raw.Count);
        foreach (var a in raw) {
            var c = CompileStringArg(a);
            if (c == null) return null;
            compiled.Add(c);
        }
        return compiled;
    }

    static string? CompileStringArg(string raw) {
        // Quoted (single or backtick) → strip and compile the inner with ExprCompiler
        // (allows $(X)/@(X)/%(X) interpolation inside the quoted literal).
        if (raw.Length >= 2 && (raw[0] == '\'' && raw[^1] == '\'' || raw[0] == '`' && raw[^1] == '`'))
            return ExprCompiler.TryCompile(raw.Substring(1, raw.Length - 2));
        return ExprCompiler.TryCompile(raw);
    }

    static string? CompileIntArg(string raw) {
        // Bare numeric literal → emit as C# int literal directly.
        if (int.TryParse(raw.Trim(), out var n)) return n.ToString();
        // Stringy form: let ExprCompiler produce the string and parse it.
        var s = CompileStringArg(raw);
        return s == null ? null : $"int.Parse({s})";
    }

    static string? ApplyMethod(string receiver, string method, List<string> args) {
        // Wrap receiver in parens so we don't get operator-precedence surprises like
        // "a + b".Contains(...) parsing as a + (b.Contains(...)).
        string R() => "(" + receiver + ")";
        // `args` come from SplitArgs already pre-compiled via ExprCompiler — they're
        // valid C# string-typed expressions. We don't re-compile them; for int-typed
        // parameter positions we wrap with int.Parse() at runtime.

        return (method, args.Count) switch {
            // Bool-returning string predicates. MSBuild's value model treats booleans
            // as "true"/"false" strings; we follow suit so downstream `== 'true'` etc. works.
            ("Contains",   1) => $"({R()}.Contains({args[0]}) ? \"true\" : \"false\")",
            ("StartsWith", 1) => $"({R()}.StartsWith({args[0]}) ? \"true\" : \"false\")",
            ("EndsWith",   1) => $"({R()}.EndsWith({args[0]}) ? \"true\" : \"false\")",
            ("Equals",     1) => $"(string.Equals({R()}, {args[0]}, StringComparison.OrdinalIgnoreCase) ? \"true\" : \"false\")",

            // String transforms.
            ("ToUpper",          0) => $"{R()}.ToUpperInvariant()",
            ("ToUpperInvariant", 0) => $"{R()}.ToUpperInvariant()",
            ("ToLower",          0) => $"{R()}.ToLowerInvariant()",
            ("ToLowerInvariant", 0) => $"{R()}.ToLowerInvariant()",
            ("Trim",             0) => $"{R()}.Trim()",
            ("TrimStart",        1) => $"{R()}.TrimStart(({args[0]}).ToCharArray())",
            ("TrimEnd",          1) => $"{R()}.TrimEnd(({args[0]}).ToCharArray())",
            ("Replace",          2) => $"{R()}.Replace({args[0]}, {args[1]})",
            ("Substring",        1) => $"{R()}.Substring(int.Parse({args[0]}))",
            ("Substring",        2) => $"{R()}.Substring(int.Parse({args[0]}), int.Parse({args[1]}))",
            ("PadLeft",          1) => $"{R()}.PadLeft(int.Parse({args[0]}))",
            ("PadRight",         1) => $"{R()}.PadRight(int.Parse({args[0]}))",
            ("IndexOf",          1) => $"({R()}.IndexOf({args[0]}).ToString())",
            ("LastIndexOf",      1) => $"({R()}.LastIndexOf({args[0]}).ToString())",
            ("get_Length",       0) => $"({R()}.Length.ToString())",
            ("Split",            1) => $"{R()}.Split(({args[0]}).ToCharArray(), StringSplitOptions.None)",

            _ => null,
        };
    }
}

static class PropertyFnCompiler {
    public static string? TryCompile(string inner, string? batchItemType = null) {
        int closeBracket = inner.IndexOf(']');
        if (closeBracket < 0) return null;
        var cls = inner.Substring(1, closeBracket - 1);
        var rest = inner.Substring(closeBracket + 1);
        if (!rest.StartsWith("::")) return null;
        rest = rest.Substring(2);
        // Detect the simplest call shape: `[Class]::Name(args)` followed by optional
        // `.Member(args)` / `.Property` / `[i]` chain.
        int paren = rest.IndexOf('(');
        int dot = rest.IndexOf('.');
        if (paren < 0 || (dot >= 0 && dot < paren)) {
            int idEnd = 0;
            while (idEnd < rest.Length && (char.IsLetterOrDigit(rest[idEnd]) || rest[idEnd] == '_')) idEnd++;
            if (idEnd == 0) return null;
            var memberName = rest.Substring(0, idEnd);
            var memberExpr = EmitStaticMember(cls, memberName);
            if (memberExpr == null) return null;
            return ChainCompiler.Apply(memberExpr, rest.Substring(idEnd));
        }
        var method = rest.Substring(0, paren);
        int depth = 0; int closeParen = -1;
        for (int i = paren; i < rest.Length; i++) {
            if (rest[i] == '(') depth++;
            else if (rest[i] == ')') { depth--; if (depth == 0) { closeParen = i; break; } }
        }
        if (closeParen < 0) return null;
        var argsBlock = rest.Substring(paren + 1, closeParen - paren - 1);
        var tail = rest.Substring(closeParen + 1);
        var args = SplitArgs(argsBlock, batchItemType);
        if (args == null) return null;
        var call = EmitCall(cls, method, args);
        if (call == null) return null;
        return ChainCompiler.Apply(call, tail);
    }

    // Static field/property access — used when `[Class]::Member` has no `(args)`.
    static string? EmitStaticMember(string cls, string member) => (cls, member) switch {
        ("System.IO.Path", "PathSeparator")          => "Path.PathSeparator.ToString()",
        ("System.IO.Path", "DirectorySeparatorChar") => "Path.DirectorySeparatorChar.ToString()",
        ("System.IO.Path", "AltDirectorySeparatorChar") => "Path.AltDirectorySeparatorChar.ToString()",
        ("System.IO.Path", "VolumeSeparatorChar")    => "Path.VolumeSeparatorChar.ToString()",
        ("System.DateTime", "UtcNow")                => "DateTime.UtcNow",
        ("System.DateTime", "Now")                   => "DateTime.Now",
        ("System.Environment", "NewLine")            => "Environment.NewLine",
        ("System.Environment", "MachineName")        => "Environment.MachineName",
        _ => null,
    };

    static List<string>? SplitArgs(string argsBlock, string? batchItemType = null) {
        var result = new List<string>();
        int i = 0;
        var current = new System.Text.StringBuilder();
        int depth = 0; bool inQuote = false;
        while (i < argsBlock.Length) {
            char c = argsBlock[i];
            if (c == '\'' && !inQuote) inQuote = true;
            else if (c == '\'' && inQuote) inQuote = false;
            else if (!inQuote) {
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0) {
                    var compiled = CompileArg(current.ToString().Trim(), batchItemType);
                    if (compiled == null) return null;
                    result.Add(compiled);
                    current.Clear();
                    i++;
                    continue;
                }
            }
            current.Append(c);
            i++;
        }
        if (current.Length > 0) {
            var compiled = CompileArg(current.ToString().Trim(), batchItemType);
            if (compiled == null) return null;
            result.Add(compiled);
        }
        return result;
    }

    static string? CompileArg(string arg, string? batchItemType = null) {
        if (arg.Length == 0) return "\"\"";
        if (arg.Length >= 2 && arg[0] == '\'' && arg[^1] == '\'')
            return ExprCompiler.TryCompile(arg.Substring(1, arg.Length - 2), batchItemType);
        if (arg.Length >= 2 && arg[0] == '`' && arg[^1] == '`')
            return ExprCompiler.TryCompile(arg.Substring(1, arg.Length - 2), batchItemType);
        return ExprCompiler.TryCompile(arg, batchItemType);
    }

    static string? EmitCall(string cls, string method, List<string> args) {
        return (cls, method, args.Count) switch {
            ("MSBuild", "ValueOrDefault", 2) => $"(string.IsNullOrEmpty({args[0]}) ? {args[1]} : {args[0]})",
            ("MSBuild", "Escape", 1)         => args[0],
            ("MSBuild", "Unescape", 1)       => args[0],
            ("MSBuild", "NormalizePath", 1)  => $"Path.GetFullPath({args[0]})",
            ("MSBuild", "NormalizePath", 2)  => $"Path.GetFullPath(Path.Combine({args[0]}, {args[1]}))",
            ("MSBuild", "NormalizePath", 3)  => $"Path.GetFullPath(Path.Combine({args[0]}, {args[1]}, {args[2]}))",
            ("MSBuild", "NormalizePath", 4)  => $"Path.GetFullPath(Path.Combine({args[0]}, {args[1]}, {args[2]}, {args[3]}))",
            ("MSBuild", "NormalizeDirectory", 1) => $"(Path.GetFullPath({args[0]}) + Path.DirectorySeparatorChar)",
            ("MSBuild", "NormalizeDirectory", 2) => $"(Path.GetFullPath(Path.Combine({args[0]}, {args[1]})) + Path.DirectorySeparatorChar)",
            ("MSBuild", "NormalizeDirectory", 3) => $"(Path.GetFullPath(Path.Combine({args[0]}, {args[1]}, {args[2]})) + Path.DirectorySeparatorChar)",
            ("MSBuild", "NormalizeDirectory", 4) => $"(Path.GetFullPath(Path.Combine({args[0]}, {args[1]}, {args[2]}, {args[3]})) + Path.DirectorySeparatorChar)",
            ("MSBuild", "MakeRelative", 2)   => $"Path.GetRelativePath({args[0]}, {args[1]})",
            ("MSBuild", "VersionGreaterThan", 2)         => $"(CondHelpers.NumericCompare({args[0]}, {args[1]}) > 0 ? \"true\" : \"false\")",
            ("MSBuild", "VersionLessThan", 2)            => $"(CondHelpers.NumericCompare({args[0]}, {args[1]}) < 0 ? \"true\" : \"false\")",
            ("MSBuild", "VersionGreaterThanOrEquals", 2) => $"(CondHelpers.NumericCompare({args[0]}, {args[1]}) >= 0 ? \"true\" : \"false\")",
            ("MSBuild", "VersionLessThanOrEquals", 2)    => $"(CondHelpers.NumericCompare({args[0]}, {args[1]}) <= 0 ? \"true\" : \"false\")",
            ("MSBuild", "VersionEquals", 2)              => $"(CondHelpers.NumericCompare({args[0]}, {args[1]}) == 0 ? \"true\" : \"false\")",
            ("MSBuild", "VersionNotEquals", 2)           => $"(CondHelpers.NumericCompare({args[0]}, {args[1]}) != 0 ? \"true\" : \"false\")",
            ("MSBuild", "GetPathOfFileAbove", _) => "\"\"",
            ("MSBuild", "GetDirectoryNameOfFileAbove", _) => "\"\"",
            // Runtime/feature predicates — we evaluate at the host machine.
            ("MSBuild", "IsOSPlatform", 1)               => $"(System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Create({args[0]}.ToUpperInvariant())) ? \"true\" : \"false\")",
            ("MSBuild", "IsOsPlatform", 1)               => $"(System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Create({args[0]}.ToUpperInvariant())) ? \"true\" : \"false\")",
            ("MSBuild", "IsOSUnixLike", 0)               => "(System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX) || System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux) ? \"true\" : \"false\")",
            ("MSBuild", "DoesTaskHostExist", _)          => "\"false\"",       // No out-of-proc task hosts in bsharp.
            ("MSBuild", "AreFeaturesEnabled", _)         => "\"true\"",        // Optimistic: assume any MSBuild feature flag the SDK probes is "enabled".
            ("MSBuild", "IsTargetFrameworkCompatible", 2) => $"(TargetFrameworkHelpers.IsCompatible({args[0]}, {args[1]}) ? \"true\" : \"false\")",
            // System.IO.Path
            ("System.IO.Path", "Combine", 2)   => $"Path.Combine({args[0]}, {args[1]})",
            ("System.IO.Path", "Combine", 3)   => $"Path.Combine({args[0]}, {args[1]}, {args[2]})",
            ("System.IO.Path", "Combine", 4)   => $"Path.Combine({args[0]}, {args[1]}, {args[2]}, {args[3]})",
            ("System.IO.Path", "GetDirectoryName", 1)        => $"(Path.GetDirectoryName({args[0]}) ?? \"\")",
            ("System.IO.Path", "GetFileName", 1)             => $"Path.GetFileName({args[0]})",
            ("System.IO.Path", "GetFileNameWithoutExtension", 1) => $"Path.GetFileNameWithoutExtension({args[0]})",
            ("System.IO.Path", "GetExtension", 1)            => $"Path.GetExtension({args[0]})",
            ("System.IO.Path", "GetFullPath", 1)             => $"Path.GetFullPath({args[0]})",
            ("System.IO.Path", "GetPathRoot", 1)             => $"(Path.GetPathRoot({args[0]}) ?? \"\")",
            ("System.IO.Path", "GetRandomFileName", 0)       => "Path.GetRandomFileName()",
            ("System.IO.Path", "GetTempFileName", 0)         => "Path.GetTempFileName()",
            ("System.IO.Path", "GetTempPath", 0)             => "Path.GetTempPath()",
            ("System.IO.File", "Exists", 1)                  => $"(File.Exists({args[0]}) ? \"true\" : \"false\")",
            ("System.IO.Directory", "Exists", 1)             => $"(Directory.Exists({args[0]}) ? \"true\" : \"false\")",
            ("System.String", "IsNullOrEmpty", 1)            => $"(string.IsNullOrEmpty({args[0]}) ? \"true\" : \"false\")",
            ("System.String", "Concat", _)                   => $"string.Concat({string.Join(", ", args)})",
            ("System.String", "Copy", 1)                     => args[0],
            // `[System.String]::new('value')` — string copy-constructor. Treated as identity
            // because MSBuild's value model is already a string.
            ("System.String", "new", 1)                      => args[0],
            ("System.Guid", "NewGuid", 0)                    => "Guid.NewGuid().ToString()",
            ("System.Version", "Parse", 1)                   => $"Version.Parse({args[0]})",
            ("System.Text.RegularExpressions.Regex", "Replace", 3) => $"System.Text.RegularExpressions.Regex.Replace({args[0]}, {args[1]}, {args[2]})",
            ("System.Text.RegularExpressions.Regex", "IsMatch", 2) => $"(System.Text.RegularExpressions.Regex.IsMatch({args[0]}, {args[1]}) ? \"true\" : \"false\")",
            _ => null,
        };
    }
}
