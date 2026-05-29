// Optional task-call recorder. When BSHARP_TASKD_TRACE=<path> is set, every
// (request, response) JSON frame pair is appended to that file as one
// `{"req":...,"resp":...}` object per line. Useful for building data-driven
// task replayers (e.g. the Go host experiment).
#nullable enable
using System;
using System.IO;
using System.Text;

namespace Bsharp.Taskd;

internal static class TaskTrace {
    static readonly object _lock = new();
    static readonly string? _path = Environment.GetEnvironmentVariable("BSHARP_TASKD_TRACE");
    public static bool Enabled => !string.IsNullOrEmpty(_path);

    public static void Record(byte[] requestJson, byte[] responseJson) {
        if (!Enabled) return;
        lock (_lock) {
            try {
                using var fs = new FileStream(_path!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(Encoding.UTF8.GetBytes("{\"req\":"));
                fs.Write(requestJson);
                fs.Write(Encoding.UTF8.GetBytes(",\"resp\":"));
                fs.Write(responseJson);
                fs.Write(Encoding.UTF8.GetBytes("}\n"));
            } catch { /* tracing is best-effort */ }
        }
    }
}
