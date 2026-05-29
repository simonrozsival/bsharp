// Dedicated daemon log writer.
//
// We can't use Console.Error for daemon-internal logging because individual
// task invocations redirect Console.Out/Console.Error to TextWriter.Null to
// keep noisy task output from leaking back through the protocol. The Log
// writer below keeps a direct handle to the daemon's log file and is used
// from anywhere inside the daemon that needs to record a diagnostic.
#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace Bsharp.Taskd;

internal static class Log {
    static TextWriter _writer = TextWriter.Null;
    static readonly object _gate = new();

    public static void SetTarget(TextWriter writer) {
        _writer = writer;
    }

    public static void WriteLine(string message) {
        lock (_gate) {
            try {
                _writer.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
                _writer.Flush();
            } catch { }
        }
    }
}

// Per-connection timing snapshot. Enabled when BSHARP_TASKD_TIMING=1.
// On each connection close the daemon logs the accumulated per-task breakdown
// so we can compare against host-side ipc timing.
internal static class DaemonTiming {
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("BSHARP_TASKD_TIMING") == "1";

    static readonly ConcurrentDictionary<string, (long readTicks, long deserTicks, long execTicks, long serTicks, long writeTicks, long reqBytes, long respBytes, int count)> _stats =
        new();

    public static void Record(string taskName, long readTicks, long deserTicks, long execTicks, long serTicks, long writeTicks, int reqBytes, int respBytes) {
        if (!Enabled) return;
        _stats.AddOrUpdate(taskName,
            (readTicks, deserTicks, execTicks, serTicks, writeTicks, reqBytes, respBytes, 1),
            (_, prev) => (
                prev.readTicks + readTicks,
                prev.deserTicks + deserTicks,
                prev.execTicks + execTicks,
                prev.serTicks + serTicks,
                prev.writeTicks + writeTicks,
                prev.reqBytes + reqBytes,
                prev.respBytes + respBytes,
                prev.count + 1));
    }

    public static void Dump(string clientLabel) {
        if (!Enabled) return;
        var freq = System.Diagnostics.Stopwatch.Frequency;
        Log.WriteLine($"=== {clientLabel} timing dump ===");
        Log.WriteLine($"{"task",-50} {"calls",6} {"read",8} {"deser",8} {"exec",8} {"ser",8} {"write",8} {"reqKB",10} {"respKB",10}");
        foreach (var kv in _stats.OrderByDescending(kv => kv.Value.execTicks).Take(20)) {
            var s = kv.Value;
            Log.WriteLine($"{kv.Key,-50} {s.count,6} {s.readTicks * 1000.0 / freq,8:F2} {s.deserTicks * 1000.0 / freq,8:F2} {s.execTicks * 1000.0 / freq,8:F2} {s.serTicks * 1000.0 / freq,8:F2} {s.writeTicks * 1000.0 / freq,8:F2} {s.reqBytes / 1024.0,10:F2} {s.respBytes / 1024.0,10:F2}");
        }
        // Also dump totals
        var read = _stats.Values.Sum(v => v.readTicks);
        var deser = _stats.Values.Sum(v => v.deserTicks);
        var exec = _stats.Values.Sum(v => v.execTicks);
        var ser = _stats.Values.Sum(v => v.serTicks);
        var write = _stats.Values.Sum(v => v.writeTicks);
        var reqBytes = _stats.Values.Sum(v => v.reqBytes);
        var respBytes = _stats.Values.Sum(v => v.respBytes);
        var count = _stats.Values.Sum(v => v.count);
        Log.WriteLine($"{"TOTAL",-50} {count,6} {read * 1000.0 / freq,8:F2} {deser * 1000.0 / freq,8:F2} {exec * 1000.0 / freq,8:F2} {ser * 1000.0 / freq,8:F2} {write * 1000.0 / freq,8:F2} {reqBytes / 1024.0,10:F2} {respBytes / 1024.0,10:F2}");
        _stats.Clear();
    }
}
