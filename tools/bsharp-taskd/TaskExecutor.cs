// Reflection-cached task executor.
//
// For each (AssemblyPath, TypeName) pair that the daemon has seen, we build a
// compiled delegate that:
//   1. constructs the task (cached `Func<object>` factory),
//   2. sets each known input property from the JSON payload via per-property
//      typed setter delegates,
//   3. calls task.Execute(),
//   4. reads back each requested output property via per-property typed
//      getter delegates and serializes the value into the response.
//
// Property setters/getters are compiled with `Expression.Lambda` once per
// (type, property) pair and stored on the per-type entry. The first invocation
// for a given task pays the JIT cost; subsequent ones cost ~one virtual call
// per property write plus the actual JsonElement parse — comparable to the
// per-project sidecar's typed dispatch.
//
// SDK ASSEMBLY VERSION DRIFT
// The MSBuild task DLLs that ship with .NET SDKs reference
// `Microsoft.Build.Framework, Version=15.1.0.0`. The daemon does NOT bundle its
// own copy of MBF / MBU because the SDK ships newer interface members
// (IMultiThreadableTask, MSBuildMultiThreadableTaskAttribute, ...) that aren't
// present in the public NuGet package. Instead, AssemblyResolver redirects all
// MBF / MBU requests to the DLLs that live next to the first task DLL we load.
// Both daemon-internal references (typeof(ITask) etc.) and SDK-task interface
// chases bind to the same SDK-shipped copy.
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Microsoft.Build.Framework;
using Bsharp.Generated.TaskModel;

namespace Bsharp.Taskd;

internal static class TaskExecutor {
    sealed class AssemblyEntry {
        public required Assembly Assembly;
        public required DateTime LastWriteUtc;
    }

    sealed class TaskEntry {
        public required Type TaskType;
        public required Func<object> Factory;
        public readonly Dictionary<string, Action<object, JsonElement>> Setters = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Func<object, JsonElement?>> Getters = new(StringComparer.OrdinalIgnoreCase);
        public Action<object, IBuildEngine>? SetBuildEngine;
        // If non-null, the task type declares a writable `TaskEnvironment` property
        // (introduced for IMultiThreadableTask). MSBuild normally sets this before
        // calling Execute; the daemon must do it too or tasks NRE inside their
        // implementations of GetAbsolutePath etc.
        public Action<object, string>? SetTaskEnvironment;
    }

    static readonly ConcurrentDictionary<string, AssemblyEntry> Assemblies = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, TaskEntry> Tasks = new(StringComparer.Ordinal);

    public static TaskResult Execute(TaskInvocation req) {
        try {
            if (string.IsNullOrEmpty(req.AssemblyPath) || string.IsNullOrEmpty(req.TypeName))
                return Fail($"task '{req.TaskName}' missing AssemblyPath or TypeName in request");
            var entry = GetOrCreateTaskEntry(req.AssemblyPath, req.TypeName);
            var task = (ITask)entry.Factory();
            entry.SetBuildEngine?.Invoke(task, DaemonBuildEngine.Current);
            entry.SetTaskEnvironment?.Invoke(task, string.IsNullOrEmpty(req.Cwd) ? Environment.CurrentDirectory : req.Cwd);
            DaemonBuildEngine.ResetForInvocation();

            foreach (var (name, value) in req.Properties) {
                if (entry.Setters.TryGetValue(name, out var setter)) {
                    setter(task, value);
                } else {
                    return Fail($"task '{req.TaskName}' property '{name}' is not supported by daemon (no settable property of a known type)");
                }
            }

            bool success;
            try { success = task.Execute(); }
            catch (Exception ex) {
                return Fail($"task '{req.TaskName}' threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }

            var resp = new TaskResult { Success = success };
            foreach (var outName in req.OutputNames) {
                if (entry.Getters.TryGetValue(outName, out var getter)) {
                    var elem = getter(task);
                    if (elem.HasValue) resp.Outputs[outName] = elem.Value;
                }
            }

            if (!success) {
                var errs = DaemonBuildEngine.Current.CapturedErrors;
                resp.Error = errs.Count > 0 ? string.Join("\n", errs) : $"task '{req.TaskName}' returned false";
            }
            return resp;
        } catch (Exception ex) {
            return Fail($"daemon error executing '{req.TaskName}': {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    static TaskResult Fail(string error) => new TaskResult { Success = false, Error = error };

    static TaskEntry GetOrCreateTaskEntry(string assemblyPath, string typeName) {
        var fullPath = Path.GetFullPath(assemblyPath);
        var key = fullPath + "::" + typeName;
        var asmEntry = GetOrCreateAssembly(fullPath);
        // If the assembly on disk has been replaced, invalidate task caches built against it.
        return Tasks.GetOrAdd(key, _ => BuildTaskEntry(asmEntry.Assembly, typeName, fullPath));
    }

    static AssemblyEntry GetOrCreateAssembly(string fullPath) {
        AssemblyResolver.EnsureInstalled();
        // Register the task DLL's directory as a probe dir BEFORE we try to load the
        // assembly. The Resolving handler will fall back to this dir when MBF / MBU
        // (or any sibling SDK dependency) is requested by the task's metadata chase.
        AssemblyResolver.RegisterProbeDirectory(Path.GetDirectoryName(fullPath) ?? "");
        var lastWrite = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : DateTime.MinValue;
        if (Assemblies.TryGetValue(fullPath, out var existing) && existing.LastWriteUtc == lastWrite)
            return existing;
        // Stale or first entry: load fresh (default ALC reuses already-loaded assemblies; that's
        // fine — wrong-SDK conflicts are prevented by the SDK-fingerprinted socket name).
        var asm = Assembly.LoadFrom(fullPath);
        var entry = new AssemblyEntry { Assembly = asm, LastWriteUtc = lastWrite };
        Assemblies[fullPath] = entry;
        // Invalidate any cached task entries that came from this assembly path; they may now
        // be stale. (Cheap: tasks are small in number.)
        foreach (var key in Tasks.Keys.Where(k => k.StartsWith(fullPath + "::", StringComparison.OrdinalIgnoreCase)).ToList())
            Tasks.TryRemove(key, out _);
        return entry;
    }

    static TaskEntry BuildTaskEntry(Assembly asm, string typeName, string assemblyPath) {
        var type = asm.GetType(typeName, throwOnError: false)
            ?? throw new InvalidOperationException($"type '{typeName}' not found in '{assemblyPath}'");
        if (!typeof(ITask).IsAssignableFrom(type))
            throw new InvalidOperationException($"type '{typeName}' does not implement Microsoft.Build.Framework.ITask");

        var ctor = type.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException($"type '{typeName}' has no parameterless constructor");
        var factory = Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(ctor), typeof(object))).Compile();

        var entry = new TaskEntry { TaskType = type, Factory = factory };

        var beProp = type.GetProperty("BuildEngine", BindingFlags.Instance | BindingFlags.Public);
        if (beProp?.SetMethod != null) {
            var objParam = Expression.Parameter(typeof(object), "obj");
            var bep = Expression.Parameter(typeof(IBuildEngine), "be");
            var assign = Expression.Call(Expression.Convert(objParam, type), beProp.SetMethod, Expression.Convert(bep, beProp.PropertyType));
            entry.SetBuildEngine = Expression.Lambda<Action<object, IBuildEngine>>(assign, objParam, bep).Compile();
        }

        // Bind `TaskEnvironment` (introduced in MBF 15.x for IMultiThreadableTask).
        // We construct one via `TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(cwd, null)`.
        // Done reflectively so the daemon doesn't take a hard reference on the property type.
        var teProp = type.GetProperty("TaskEnvironment", BindingFlags.Instance | BindingFlags.Public);
        if (teProp?.SetMethod != null && teProp.PropertyType.FullName == "Microsoft.Build.Framework.TaskEnvironment") {
            var factoryMethod = teProp.PropertyType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "CreateWithProjectDirectoryAndEnvironment");
            if (factoryMethod != null) {
                var factoryParams = factoryMethod.GetParameters();
                var objParam = Expression.Parameter(typeof(object), "obj");
                var cwdParam = Expression.Parameter(typeof(string), "cwd");
                Expression[] callArgs = new Expression[factoryParams.Length];
                callArgs[0] = cwdParam;
                for (int i = 1; i < factoryParams.Length; i++) {
                    var p = factoryParams[i];
                    callArgs[i] = p.HasDefaultValue
                        ? Expression.Constant(p.DefaultValue, p.ParameterType)
                        : Expression.Default(p.ParameterType);
                }
                var createCall = Expression.Call(factoryMethod, callArgs);
                var assign = Expression.Call(Expression.Convert(objParam, type), teProp.SetMethod, createCall);
                entry.SetTaskEnvironment = Expression.Lambda<Action<object, string>>(assign, objParam, cwdParam).Compile();
            }
        }

        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
            var pname = prop.Name;
            if (pname == "BuildEngine" || (pname.StartsWith("BuildEngine", StringComparison.Ordinal) && pname.Length > 11 && char.IsDigit(pname[11])))
                continue;
            if (pname == "HostObject" || pname == "Log" || pname == "TaskEnvironment")
                continue;
            if (prop.SetMethod != null && prop.SetMethod.IsPublic) {
                var setter = TryBuildSetter(type, prop);
                if (setter != null) entry.Setters[pname] = setter;
            }
            if (prop.GetMethod != null && prop.GetMethod.IsPublic && Attribute.IsDefined(prop, typeof(OutputAttribute))) {
                var getter = TryBuildGetter(type, prop);
                if (getter != null) entry.Getters[pname] = getter;
            }
        }
        return entry;
    }

    static Action<object, JsonElement>? TryBuildSetter(Type taskType, PropertyInfo prop) {
        var pt = prop.PropertyType;
        var objParam = Expression.Parameter(typeof(object), "obj");
        var elemParam = Expression.Parameter(typeof(JsonElement), "elem");
        var typedTask = Expression.Convert(objParam, taskType);

        Expression? valueExpr = null;
        if (pt == typeof(string)) valueExpr = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(ReadString), BindingFlags.Static | BindingFlags.NonPublic)!, elemParam);
        else if (pt == typeof(bool) || pt == typeof(bool?)) valueExpr = Expression.Convert(Expression.Call(typeof(TaskExecutor).GetMethod(nameof(ReadBool), BindingFlags.Static | BindingFlags.NonPublic)!, elemParam), pt);
        else if (pt == typeof(int) || pt == typeof(int?)) valueExpr = Expression.Convert(Expression.Call(typeof(TaskExecutor).GetMethod(nameof(ReadInt), BindingFlags.Static | BindingFlags.NonPublic)!, elemParam), pt);
        else if (pt == typeof(long) || pt == typeof(long?)) valueExpr = Expression.Convert(Expression.Call(typeof(TaskExecutor).GetMethod(nameof(ReadLong), BindingFlags.Static | BindingFlags.NonPublic)!, elemParam), pt);
        else if (pt == typeof(double) || pt == typeof(double?)) valueExpr = Expression.Convert(Expression.Call(typeof(TaskExecutor).GetMethod(nameof(ReadDouble), BindingFlags.Static | BindingFlags.NonPublic)!, elemParam), pt);
        else if (pt == typeof(string[])) valueExpr = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(ReadStrings), BindingFlags.Static | BindingFlags.NonPublic)!, elemParam);
        else if (pt == typeof(ITaskItem) || pt == typeof(ITaskItem2)) valueExpr = Expression.Convert(Expression.Call(typeof(TaskExecutor).GetMethod(nameof(ReadItem), BindingFlags.Static | BindingFlags.NonPublic)!, elemParam), pt);
        else if (pt == typeof(ITaskItem[])) valueExpr = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(ReadItems), BindingFlags.Static | BindingFlags.NonPublic)!, elemParam);
        else if (pt == typeof(ITaskItem2[])) valueExpr = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(ReadItems2), BindingFlags.Static | BindingFlags.NonPublic)!, elemParam);
        else if (pt.IsEnum) {
            var stringRead = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(ReadString), BindingFlags.Static | BindingFlags.NonPublic)!, elemParam);
            valueExpr = Expression.Call(typeof(Enum).GetMethod(nameof(Enum.Parse), new[] { typeof(Type), typeof(string), typeof(bool) })!,
                Expression.Constant(pt), stringRead, Expression.Constant(true));
            valueExpr = Expression.Convert(valueExpr, pt);
        }

        if (valueExpr == null) return null;
        var assign = Expression.Call(typedTask, prop.SetMethod!, valueExpr);
        return Expression.Lambda<Action<object, JsonElement>>(assign, objParam, elemParam).Compile();
    }

    static Func<object, JsonElement?>? TryBuildGetter(Type taskType, PropertyInfo prop) {
        var pt = prop.PropertyType;
        var objParam = Expression.Parameter(typeof(object), "obj");
        var typedTask = Expression.Convert(objParam, taskType);
        var getValue = Expression.Property(typedTask, prop);

        Expression? body = null;
        if (pt == typeof(string)) body = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(WriteString), BindingFlags.Static | BindingFlags.NonPublic)!, getValue);
        else if (pt == typeof(bool)) body = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(WriteBool), BindingFlags.Static | BindingFlags.NonPublic)!, getValue);
        else if (pt == typeof(int)) body = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(WriteInt), BindingFlags.Static | BindingFlags.NonPublic)!, getValue);
        else if (pt == typeof(long)) body = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(WriteLong), BindingFlags.Static | BindingFlags.NonPublic)!, getValue);
        else if (pt == typeof(double)) body = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(WriteDouble), BindingFlags.Static | BindingFlags.NonPublic)!, getValue);
        else if (pt == typeof(string[])) body = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(WriteStrings), BindingFlags.Static | BindingFlags.NonPublic)!, getValue);
        else if (typeof(ITaskItem).IsAssignableFrom(pt) && !pt.IsArray)
            body = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(WriteSingleItemAsArray), BindingFlags.Static | BindingFlags.NonPublic)!, Expression.Convert(getValue, typeof(ITaskItem)));
        else if (pt.IsArray && typeof(ITaskItem).IsAssignableFrom(pt.GetElementType()!))
            body = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(WriteItems), BindingFlags.Static | BindingFlags.NonPublic)!, Expression.Convert(getValue, typeof(ITaskItem[])));
        else
            body = Expression.Call(typeof(TaskExecutor).GetMethod(nameof(WriteObjectAsString), BindingFlags.Static | BindingFlags.NonPublic)!,
                Expression.Convert(getValue, typeof(object)));

        return Expression.Lambda<Func<object, JsonElement?>>(body, objParam).Compile();
    }

    // --- JSON readers (input-side) ---
    static string ReadString(JsonElement e) {
        if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
        return e.ToString();
    }
    static bool ReadBool(JsonElement e) {
        if (e.ValueKind == JsonValueKind.True) return true;
        if (e.ValueKind == JsonValueKind.False) return false;
        if (e.ValueKind == JsonValueKind.String && bool.TryParse(e.GetString(), out var b)) return b;
        return false;
    }
    static int ReadInt(JsonElement e) => e.TryGetInt32(out var v) ? v : 0;
    static long ReadLong(JsonElement e) => e.TryGetInt64(out var v) ? v : 0L;
    static double ReadDouble(JsonElement e) => e.TryGetDouble(out var v) ? v : 0d;
    static string[] ReadStrings(JsonElement e) {
        if (e.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return JsonSerializer.Deserialize(e, TaskModelJson.Default.StringArray) ?? Array.Empty<string>();
    }
    static ITaskItem? ReadItem(JsonElement e) {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var spec = JsonSerializer.Deserialize(e, TaskModelJson.Default.ItemSpec);
        return spec == null ? null : new DaemonTaskItem(spec);
    }
    static ITaskItem[] ReadItems(JsonElement e) {
        if (e.ValueKind != JsonValueKind.Array) return Array.Empty<ITaskItem>();
        var specs = JsonSerializer.Deserialize(e, TaskModelJson.Default.ItemSpecArray) ?? Array.Empty<ItemSpec>();
        var arr = new ITaskItem[specs.Length];
        for (var i = 0; i < specs.Length; i++) arr[i] = new DaemonTaskItem(specs[i]);
        return arr;
    }
    static ITaskItem2[] ReadItems2(JsonElement e) {
        if (e.ValueKind != JsonValueKind.Array) return Array.Empty<ITaskItem2>();
        var specs = JsonSerializer.Deserialize(e, TaskModelJson.Default.ItemSpecArray) ?? Array.Empty<ItemSpec>();
        var arr = new ITaskItem2[specs.Length];
        for (var i = 0; i < specs.Length; i++) arr[i] = new DaemonTaskItem(specs[i]);
        return arr;
    }

    // --- JSON writers (output-side) ---
    static JsonElement? WriteString(string? v) => v == null ? null : JsonSerializer.SerializeToElement(v, TaskModelJson.Default.String);
    static JsonElement? WriteBool(bool v) => JsonSerializer.SerializeToElement(v ? "true" : "false", TaskModelJson.Default.String);
    static JsonElement? WriteInt(int v) => JsonSerializer.SerializeToElement(v.ToString(System.Globalization.CultureInfo.InvariantCulture), TaskModelJson.Default.String);
    static JsonElement? WriteLong(long v) => JsonSerializer.SerializeToElement(v.ToString(System.Globalization.CultureInfo.InvariantCulture), TaskModelJson.Default.String);
    static JsonElement? WriteDouble(double v) => JsonSerializer.SerializeToElement(v.ToString(System.Globalization.CultureInfo.InvariantCulture), TaskModelJson.Default.String);
    static JsonElement? WriteStrings(string[]? v) => v == null
        ? JsonSerializer.SerializeToElement(Array.Empty<string>(), TaskModelJson.Default.StringArray)
        : JsonSerializer.SerializeToElement(v, TaskModelJson.Default.StringArray);
    static JsonElement? WriteSingleItemAsArray(ITaskItem? item) {
        if (item == null) return null;
        var arr = new[] { DaemonTaskItem.ToSpec(item) };
        return JsonSerializer.SerializeToElement(arr, TaskModelJson.Default.ItemSpecArray);
    }
    static JsonElement? WriteItems(ITaskItem[]? items) {
        if (items == null) return JsonSerializer.SerializeToElement(Array.Empty<ItemSpec>(), TaskModelJson.Default.ItemSpecArray);
        var specs = items.Where(i => i != null).Select(i => DaemonTaskItem.ToSpec(i!)).ToArray();
        return JsonSerializer.SerializeToElement(specs, TaskModelJson.Default.ItemSpecArray);
    }
    static JsonElement? WriteObjectAsString(object? v) {
        var s = v == null ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return JsonSerializer.SerializeToElement(s, TaskModelJson.Default.String);
    }
}
