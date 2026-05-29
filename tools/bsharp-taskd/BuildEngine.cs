// IBuildEngine implementation + ITaskItem adapter used by the daemon.
//
// Tasks call methods on `IBuildEngine` while they run. The daemon provides a
// permissive implementation that captures error messages so they can be
// reported back to the host via TaskResult.Error, and silently discards
// everything else (warnings, messages, custom events). This mirrors the
// per-project task server's `BsharpBuildEngine` behavior.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Bsharp.Generated.TaskModel;

namespace Bsharp.Taskd;

internal static class WellKnownMetadata {
    static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase) {
        "FullPath", "RootDir", "Filename", "Extension", "RelativeDir", "Directory",
        "RecursiveDir", "Identity", "ModifiedTime", "CreatedTime", "AccessedTime",
        "DefiningProjectFullPath", "DefiningProjectDirectory",
        "DefiningProjectName", "DefiningProjectExtension",
    };
    public static bool IsWellKnown(string name) => Names.Contains(name);
}

internal sealed class DaemonTaskItem : ITaskItem, ITaskItem2 {
    readonly ItemSpec _spec;
    public DaemonTaskItem(ItemSpec spec) {
        _spec = spec;
        if (spec.Metadata != null && !ReferenceEquals(spec.Metadata.Comparer, StringComparer.OrdinalIgnoreCase)) {
            spec.Metadata = new Dictionary<string, string>(spec.Metadata, StringComparer.OrdinalIgnoreCase);
        }
    }
    public ItemSpec Inner => _spec;
    public string ItemSpec {
        get => _spec.Identity;
        set => _spec.Identity = value;
    }
    public System.Collections.ICollection MetadataNames =>
        _spec.Metadata?.Keys ?? (System.Collections.ICollection)Array.Empty<string>();
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
        _ => _spec.Metadata != null && _spec.Metadata.TryGetValue(name!, out var v) ? v : "",
    };
    public void SetMetadata(string name, string value) {
        (_spec.Metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[name] = value ?? "";
    }
    public void RemoveMetadata(string name) => _spec.Metadata?.Remove(name);
    public void CopyMetadataTo(ITaskItem destinationItem) {
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

    public static ItemSpec ToSpec(ITaskItem item) {
        var spec = new ItemSpec { Identity = item.ItemSpec };
        foreach (var k in item.MetadataNames) {
            var key = k?.ToString() ?? "";
            if (key.Length > 0 && !WellKnownMetadata.IsWellKnown(key))
                (spec.Metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[key] = item.GetMetadata(key) ?? "";
        }
        return spec;
    }
}

internal sealed class DaemonBuildEngine
    : IBuildEngine, IBuildEngine2, IBuildEngine3, IBuildEngine4, IBuildEngine5,
      IBuildEngine6, IBuildEngine7, IBuildEngine8, IBuildEngine9, IBuildEngine10
{
    [ThreadStatic] static DaemonBuildEngine? _current;
    public static DaemonBuildEngine Current => _current ??= new();
    public static void ResetForInvocation() {
        var inst = Current;
        inst._capturedErrors.Clear();
        inst._taskObjs.Clear();
    }

    readonly List<string> _capturedErrors = new();
    public IReadOnlyList<string> CapturedErrors => _capturedErrors;

    public string ProjectFileOfTaskNode => "";
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public bool ContinueOnError => false;
    public bool IsRunningMultipleNodes => false;

    public void LogMessageEvent(BuildMessageEventArgs e) { }
    public void LogWarningEvent(BuildWarningEventArgs e) { }
    public void LogErrorEvent(BuildErrorEventArgs e) {
        var line = string.IsNullOrEmpty(e.Code) ? $"error: {e.Message}" : $"error {e.Code}: {e.Message}";
        _capturedErrors.Add(line);
    }
    public void LogCustomEvent(CustomBuildEventArgs e) { }

    public bool BuildProjectFile(string projectFileName, string[]? targetNames, System.Collections.IDictionary? globalProperties, System.Collections.IDictionary? targetOutputs) {
        if (string.IsNullOrEmpty(projectFileName)) return true;
        throw new NotSupportedException("<MSBuild> recursive task is not supported in the task daemon");
    }
    public bool BuildProjectFile(string projectFileName, string[]? targetNames, System.Collections.IDictionary? globalProperties, System.Collections.IDictionary? targetOutputs, string? toolsVersion)
        => BuildProjectFile(projectFileName, targetNames, globalProperties, targetOutputs);
    public bool BuildProjectFilesInParallel(string[] projectFileNames, string[] targetNames, System.Collections.IDictionary[] globalProperties, System.Collections.IDictionary[] targetOutputsPerProject, string[] toolsVersion, bool useResultsCache, bool unloadProjectsOnCompletion) {
        if (projectFileNames == null || projectFileNames.Length == 0) return true;
        throw new NotSupportedException("BuildProjectFilesInParallel");
    }
    public BuildEngineResult BuildProjectFilesInParallel(string[] projectFileNames, string[] targetNames, System.Collections.IDictionary[] globalProperties, IList<string>[] removeGlobalProperties, string[] toolsVersion, bool returnTargetOutputs) {
        if (projectFileNames == null || projectFileNames.Length == 0)
            return new BuildEngineResult(true, new List<IDictionary<string, ITaskItem[]>>());
        throw new NotSupportedException("BuildProjectFilesInParallel");
    }

    public void Yield() { }
    public void Reacquire() { }

    readonly Dictionary<(string, RegisteredTaskObjectLifetime), object> _taskObjs = new();
    public void RegisterTaskObject(object key, object obj, RegisteredTaskObjectLifetime lifetime, bool allowEarlyCollection)
        => _taskObjs[(key?.ToString() ?? "", lifetime)] = obj;
    public object? GetRegisteredTaskObject(object key, RegisteredTaskObjectLifetime lifetime)
        => _taskObjs.TryGetValue((key?.ToString() ?? "", lifetime), out var v) ? v : null;
    public object? UnregisterTaskObject(object key, RegisteredTaskObjectLifetime lifetime) {
        var k = (key?.ToString() ?? "", lifetime);
        if (_taskObjs.TryGetValue(k, out var v)) { _taskObjs.Remove(k); return v; }
        return null;
    }

    public void LogTelemetry(string eventName, IDictionary<string, string> properties) { }
    public IReadOnlyDictionary<string, string> GetGlobalProperties() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool AllowFailureWithoutError { get; set; }
    public bool ShouldTreatWarningAsError(string warningCode) => false;
    public int RequestCores(int requestedCores) => requestedCores;
    public void ReleaseCores(int coresToRelease) { }
    public EngineServices EngineServices => DaemonEngineServices.Instance;
}

internal sealed class DaemonEngineServices : EngineServices {
    public static readonly DaemonEngineServices Instance = new();
    public override bool LogsMessagesOfImportance(MessageImportance importance) => true;
    public override bool IsTaskInputLoggingEnabled => false;
}
