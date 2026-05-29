// Shared task invocation model for bsharp's universal task daemon.
//
// This file is the single source of truth for the wire protocol between the
// generated bsharp host and `bsharp-taskd`. It is compiled directly into the
// daemon, embedded as a resource in the codegen tool, and written verbatim
// into the generated host source tree (`.bsharp/TaskModel.cs`) at codegen
// time.
//
// PROTOCOL VERSION HISTORY
//   1 — original per-project sidecar (TaskInvocation { TaskName, Properties })
//   2 — universal daemon: adds AssemblyPath, TypeName, OutputNames, Cwd to
//       TaskInvocation; introduces HandshakeRequest/Response.
//
// Any change here must bump TaskModel.ProtocolVersion. The socket path
// includes the version so old daemons cannot accidentally serve new clients.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bsharp.Generated.TaskModel;

public static class TaskModel {
    public const int ProtocolVersion = 2;
}

public sealed class HandshakeRequest {
    public int ProtocolVersion { get; set; }
    public string SdkFingerprint { get; set; } = "";
    public string DaemonVersion { get; set; } = "";
}

public sealed class HandshakeResponse {
    public int ProtocolVersion { get; set; }
    public string DaemonVersion { get; set; } = "";
    public string? Error { get; set; }
}

public sealed class TaskInvocation {
    public string TaskName { get; set; } = "";
    public string? TargetName { get; set; }
    public string AssemblyPath { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string[] OutputNames { get; set; } = Array.Empty<string>();
    public string Cwd { get; set; } = "";
    public Dictionary<string, JsonElement> Properties { get; set; } = new();
}

public sealed class TaskResult {
    public bool Success { get; set; }
    public Dictionary<string, JsonElement> Outputs { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class ItemSpec {
    public string Identity { get; set; } = "";
    public Dictionary<string, string>? Metadata { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HandshakeRequest))]
[JsonSerializable(typeof(HandshakeResponse))]
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

public static class TaskModelExt {
    public static void SetString(this TaskInvocation r, string name, string value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(NormalizeTaskString(name, value), TaskModelJson.Default.String);
    public static void SetBool(this TaskInvocation r, string name, bool value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.Boolean);
    public static void SetInt(this TaskInvocation r, string name, int value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.Int32);
    public static void SetLong(this TaskInvocation r, string name, long value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.Int64);
    public static void SetDouble(this TaskInvocation r, string name, double value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.Double);
    public static void SetStrings(this TaskInvocation r, string name, string[] value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(NormalizeTaskStrings(name, value), TaskModelJson.Default.StringArray);
    public static void SetItem(this TaskInvocation r, string name, ItemSpec? value) {
        if (value == null) return;
        r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.ItemSpec);
    }
    public static void SetItems(this TaskInvocation r, string name, ItemSpec[] value)
        => r.Properties[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.ItemSpecArray);

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
    public static bool TryGetString(this TaskInvocation r, string name, out string value) {
        if (r.Properties.TryGetValue(name, out var e)) {
            value = e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : e.ToString();
            return true;
        }
        value = "";
        return false;
    }
    public static bool TryGetBool(this TaskInvocation r, string name, out bool value) {
        if (r.Properties.TryGetValue(name, out var e)) {
            value = e.ValueKind == JsonValueKind.True || (e.ValueKind == JsonValueKind.String && bool.TryParse(e.GetString(), out var b) && b);
            return true;
        }
        value = false;
        return false;
    }
    public static bool TryGetInt(this TaskInvocation r, string name, out int value) {
        if (r.Properties.TryGetValue(name, out var e)) return e.TryGetInt32(out value);
        value = 0;
        return false;
    }
    public static bool TryGetLong(this TaskInvocation r, string name, out long value) {
        if (r.Properties.TryGetValue(name, out var e)) return e.TryGetInt64(out value);
        value = 0;
        return false;
    }
    public static bool TryGetDouble(this TaskInvocation r, string name, out double value) {
        if (r.Properties.TryGetValue(name, out var e)) return e.TryGetDouble(out value);
        value = 0d;
        return false;
    }
    public static bool TryGetStrings(this TaskInvocation r, string name, out string[] value) {
        if (r.Properties.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.Array) {
            value = JsonSerializer.Deserialize(e, TaskModelJson.Default.StringArray) ?? Array.Empty<string>();
            return true;
        }
        value = Array.Empty<string>();
        return false;
    }
    public static bool TryGetItem(this TaskInvocation r, string name, out ItemSpec? value) {
        if (r.Properties.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.Object) {
            value = JsonSerializer.Deserialize(e, TaskModelJson.Default.ItemSpec);
            return true;
        }
        value = null;
        return false;
    }
    public static bool TryGetItems(this TaskInvocation r, string name, out ItemSpec[] value) {
        if (r.Properties.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.Array) {
            value = JsonSerializer.Deserialize(e, TaskModelJson.Default.ItemSpecArray) ?? Array.Empty<ItemSpec>();
            return true;
        }
        value = Array.Empty<ItemSpec>();
        return false;
    }

    public static void SetString(this TaskResult r, string name, string? value) {
        if (value == null) return;
        r.Outputs[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.String);
    }
    public static void SetBool(this TaskResult r, string name, bool value)
        => r.Outputs[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.Boolean);
    public static void SetInt(this TaskResult r, string name, int value)
        => r.Outputs[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.Int32);
    public static void SetLong(this TaskResult r, string name, long value)
        => r.Outputs[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.Int64);
    public static void SetDouble(this TaskResult r, string name, double value)
        => r.Outputs[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.Double);
    public static void SetStrings(this TaskResult r, string name, string[] value)
        => r.Outputs[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.StringArray);
    public static void SetItem(this TaskResult r, string name, ItemSpec? value) {
        if (value == null) return;
        r.Outputs[name] = JsonSerializer.SerializeToElement(value, TaskModelJson.Default.ItemSpec);
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

    static string NormalizeTaskString(string name, string value) {
        if (value.IndexOf('\\') < 0 || !IsPathLikeName(name)) return value;
        return value.Replace('\\', Path.DirectorySeparatorChar);
    }
    static string[] NormalizeTaskStrings(string name, string[] values) {
        if (!IsPathLikeName(name)) return values;
        string[]? normalized = null;
        for (int i = 0; i < values.Length; i++) {
            var value = values[i];
            if (value.IndexOf('\\') < 0) continue;
            normalized ??= (string[])values.Clone();
            normalized[i] = value.Replace('\\', Path.DirectorySeparatorChar);
        }
        return normalized ?? values;
    }
    static bool IsPathLikeName(string name) =>
        name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Directory", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("File", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Manifest", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Jar", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Zip", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Apk", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Archive", StringComparison.OrdinalIgnoreCase);
}
