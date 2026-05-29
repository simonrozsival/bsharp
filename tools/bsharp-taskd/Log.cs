// Dedicated daemon log writer.
//
// We can't use Console.Error for daemon-internal logging because individual
// task invocations redirect Console.Out/Console.Error to TextWriter.Null to
// keep noisy task output from leaking back through the protocol. The Log
// writer below keeps a direct handle to the daemon's log file and is used
// from anywhere inside the daemon that needs to record a diagnostic.
#nullable enable
using System;
using System.IO;

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
