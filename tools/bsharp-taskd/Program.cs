// Bsharp universal task daemon (`bsharp-taskd`).
//
// One long-lived process per (user, SDK fingerprint). The daemon binds a
// per-user Unix domain socket, accepts client connections from generated bsharp
// hosts, and dispatches MSBuild `ITask` invocations against task DLLs the
// client requests by absolute path. Reflection metadata for each task is
// cached and reused across builds.
//
// USAGE
//   bsharp-taskd --sdk-fingerprint <hex> [--idle-min N]
//
// LIFECYCLE
//   1. Acquire an exclusive `FileStream.Lock` on the daemon lock file. If
//      another instance already holds it, exit quietly (the client will then
//      connect to the existing daemon).
//   2. Bind a Unix domain socket at the user-private path computed from the
//      fingerprint. Stale socket files are unlinked.
//   3. Write our PID to the pid file.
//   4. Accept connections. Each connection runs in its own task but tasks
//      themselves are dispatched under a single semaphore — MSBuild task code
//      mutates process-global state (cwd, Console) so parallel execution is
//      unsafe in v1.
//   5. Reset the idle timer on every accept. When idle for IdleMinutes minutes
//      with zero active connections, shut down cleanly.
//
// PROTOCOL
//   1. Client sends a `HandshakeRequest` with its protocol version and SDK
//      fingerprint. Daemon validates and replies with `HandshakeResponse`.
//   2. Loop: client sends `TaskInvocation`, daemon replies with `TaskResult`.
//   3. Client closes the socket; daemon drops the connection (and decrements
//      the active-connection count).
#nullable enable
using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bsharp.Generated.TaskModel;

namespace Bsharp.Taskd;

public static class Program {
    [DllImport("libc", SetLastError = true)]
    static extern int flock(int fd, int operation);
    const int LOCK_EX = 2;
    const int LOCK_NB = 4;

    // POSIX `close` for releasing inherited file descriptors that we never want
    // the daemon to hold open. macOS keeps `dup`'d fds from the spawning shell
    // alive in the child (e.g. the launcher's stdout pipe via `_NSGetExecutablePath`-style
    // helpers); failing to close them keeps the parent's pipeline open forever
    // after the launcher exits.
    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);
    [DllImport("libc", SetLastError = true)]
    static extern int open(string path, int flags, int mode);
    [DllImport("libc", SetLastError = true)]
    static extern int dup2(int oldfd, int newfd);
    // setsid(2): create a new session and process group with the calling process
    // as leader. Without this, the daemon stays in the spawning shell's process
    // group and dies on SIGHUP when that shell exits (very common when bsharp
    // is invoked from a script or a `bash -c` subshell). setsid only succeeds
    // when the caller is NOT already a process-group leader, which is true
    // for us because we were exec'd by /bin/sh -c "exec bsharp-taskd ...".
    [DllImport("libc", SetLastError = true)]
    static extern int setsid();
    const int O_RDONLY = 0x0000;
    const int O_WRONLY = 0x0001;
    const int O_CREAT = 0x0200; // macOS value
    const int O_APPEND = 0x0008; // macOS value

    static readonly SemaphoreSlim ExecutionLock = new(1, 1);
    static int _activeConnections;
    static DateTime _lastActivityUtc = DateTime.UtcNow;
    static int IdleMinutes = 10;
    static string _expectedFingerprint = "";

    public static async Task<int> Main(string[] args) {
        // Detach from the spawner's stdio. The host spawns us with
        // Process.Start(RedirectStandard*=false) which inherits the launcher's
        // stdout/stderr file descriptors — typically a pipe to a parent process
        // (e.g. `bsharp build | tail`). Keeping those fds open in this
        // long-lived daemon would pin the parent pipeline alive after the
        // launcher and host exit. Redirect stdin to /dev/null and stdout/stderr
        // to the daemon log file, then close every other inherited fd so the
        // daemon owns nothing tied to the spawning process.
        DetachFromSpawner();

        string? fingerprint = null;
        for (var i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--sdk-fingerprint":
                    if (i + 1 >= args.Length) return Fatal("--sdk-fingerprint requires an argument");
                    fingerprint = args[++i];
                    break;
                case "--idle-min":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out IdleMinutes))
                        return Fatal("--idle-min requires an integer argument");
                    break;
                case "--version":
                    Console.WriteLine($"bsharp-taskd {DaemonPaths.DaemonVersion} (protocol {TaskModel.ProtocolVersion})");
                    return 0;
                case "--help":
                case "-h":
                    Console.WriteLine("usage: bsharp-taskd --sdk-fingerprint <hex> [--idle-min N]");
                    return 0;
                default:
                    return Fatal($"unknown argument: {args[i]}");
            }
        }
        if (string.IsNullOrWhiteSpace(fingerprint)) return Fatal("missing required --sdk-fingerprint");
        if (Environment.GetEnvironmentVariable("BSHARP_TASKD_IDLE_MIN") is { Length: > 0 } envIdle
            && int.TryParse(envIdle, out var i2)) IdleMinutes = i2;
        _expectedFingerprint = fingerprint;

        var lockPath = DaemonPaths.GetLockPath(fingerprint);
        var pidPath = DaemonPaths.GetPidPath(fingerprint);
        var socketPath = DaemonPaths.GetSocketPath(fingerprint);
        var logPath = DaemonPaths.GetLogPath(fingerprint);

        // Open the log file early so we capture startup failures.
        // Also dup2 the log fd onto OS fds 1 and 2 so any direct write(2) — for
        // example from libc/CoreCLR diagnostics that bypass System.Console — is
        // captured and, crucially, no inherited pipe end remains on stdout/stderr.
        StreamWriter? logWriter = null;
        try {
            var logStream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            logWriter = new StreamWriter(logStream) { AutoFlush = true };
            Log.SetTarget(logWriter);
            Console.SetOut(logWriter);
            Console.SetError(logWriter);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                var logFd = (int)logStream.SafeFileHandle.DangerousGetHandle();
                if (logFd > 0) {
                    _ = dup2(logFd, 1);
                    _ = dup2(logFd, 2);
                }
            }
        } catch { /* best-effort logging */ }

        // Cross-platform single-instance: hold a file descriptor open and apply an
        // advisory exclusive lock via flock(2). FileShare.None on macOS isn't enforced
        // across processes, so we use flock directly. (Windows isn't supported by this
        // daemon yet — bsharp is osx-arm64 first.)
        FileStream? lockStream;
        try {
            lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        } catch (IOException) {
            // Another process already opened this file with an incompatible FileShare.
            // Either way, a sibling daemon owns the slot — exit quietly so the client
            // can connect to it.
            Log.WriteLine("lock file held by another process — exiting");
            return 0;
        } catch (Exception ex) {
            Log.WriteLine($"could not open lock file '{lockPath}': {ex.Message}");
            return 1;
        }
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            var fd = (int)lockStream.SafeFileHandle.DangerousGetHandle();
            if (flock(fd, LOCK_EX | LOCK_NB) != 0) {
                Log.WriteLine("another daemon already holds the lock — exiting");
                lockStream.Dispose();
                return 0;
            }
        }

        // Stale socket file from a crashed predecessor: unlink before binding.
        if (File.Exists(socketPath)) {
            try { File.Delete(socketPath); } catch { }
        }

        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try {
            listener.Bind(endpoint);
            listener.Listen(64);
        } catch (Exception ex) {
            Log.WriteLine($"bind/listen on '{socketPath}' failed: {ex.Message}");
            return 1;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            try { File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }

        try { File.WriteAllText(pidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
        Log.WriteLine($"pid={Environment.ProcessId} listening on {socketPath} fingerprint={fingerprint} idle={IdleMinutes}m");

        // Install the assembly resolver early so the first task request doesn't race
        // with metadata-driven assembly loads inside the JIT.
        AssemblyResolver.EnsureInstalled();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var idleTask = MonitorIdleAsync(cts);
        try {
            while (!cts.IsCancellationRequested) {
                Socket client;
                try {
                    client = await listener.AcceptAsync(cts.Token);
                } catch (OperationCanceledException) { break; }
                catch (Exception ex) {
                    Log.WriteLine($"accept failed: {ex.Message}");
                    continue;
                }
                Interlocked.Increment(ref _activeConnections);
                _lastActivityUtc = DateTime.UtcNow;
                // Run on the thread pool so the listener loop is never blocked even
                // briefly by a slow handshake on a freshly-accepted connection.
                _ = Task.Run(() => HandleClientAsync(client));
            }
        } finally {
            try { listener.Close(); } catch { }
            try { File.Delete(socketPath); } catch { }
            try { File.Delete(pidPath); } catch { }
            try { lockStream.Dispose(); } catch { }
        }
        await idleTask;
        return 0;
    }

    static async Task HandleClientAsync(Socket client) {
        try {
            using (client) {
                using var stream = new NetworkStream(client, ownsSocket: false);
                var handshakeBytes = await FrameProtocol.ReadFrameAsync(stream).ConfigureAwait(false);
                if (handshakeBytes == null) return;
                var handshake = JsonSerializer.Deserialize(handshakeBytes, TaskModelJson.Default.HandshakeRequest) ?? new HandshakeRequest();
                var response = new HandshakeResponse {
                    ProtocolVersion = TaskModel.ProtocolVersion,
                    DaemonVersion = DaemonPaths.DaemonVersion,
                };
                if (handshake.ProtocolVersion != TaskModel.ProtocolVersion)
                    response.Error = $"protocol version mismatch: client={handshake.ProtocolVersion}, daemon={TaskModel.ProtocolVersion}";
                else if (!string.IsNullOrEmpty(handshake.SdkFingerprint) && handshake.SdkFingerprint != _expectedFingerprint)
                    response.Error = $"sdk fingerprint mismatch: client={handshake.SdkFingerprint}, daemon={_expectedFingerprint}";
                await FrameProtocol.WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(response, TaskModelJson.Default.HandshakeResponse)).ConfigureAwait(false);
                if (response.Error != null) return;

                while (true) {
                    var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var payload = await FrameProtocol.ReadFrameAsync(stream).ConfigureAwait(false);
                    if (payload == null) return;
                    var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var req = JsonSerializer.Deserialize(payload, TaskModelJson.Default.TaskInvocation) ?? new TaskInvocation();
                    var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var resp = await ExecuteSerializedAsync(req).ConfigureAwait(false);
                    var t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(resp, TaskModelJson.Default.TaskResult);
                    var t4 = System.Diagnostics.Stopwatch.GetTimestamp();
                    await FrameProtocol.WriteFrameAsync(stream, bytes).ConfigureAwait(false);
                    var t5 = System.Diagnostics.Stopwatch.GetTimestamp();
                    _lastActivityUtc = DateTime.UtcNow;
                    if (DaemonTiming.Enabled) DaemonTiming.Record(req.TaskName, t1 - t0, t2 - t1, t3 - t2, t4 - t3, t5 - t4, payload.Length, bytes.Length);
                }
            }
        } catch (EndOfStreamException) {
            // Normal client close mid-frame; ignore.
        } catch (Exception ex) {
            Log.WriteLine($"client connection ended with error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        } finally {
            DaemonTiming.Dump("client closed");
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    static readonly string _stableHomeDir = ResolveStableHomeDir();

    static string ResolveStableHomeDir() {
        var tmp = Path.GetTempPath();
        try { if (Directory.Exists(tmp)) return tmp; } catch { }
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "C:\\" : "/";
    }

    static async Task<TaskResult> ExecuteSerializedAsync(TaskInvocation req) {
        await ExecutionLock.WaitAsync();
        try {
            // Cwd + Console redirect are process-global. We redirect Console here so any
            // task output is silenced; cwd is set per invocation and restored to a stable
            // home (tmp dir) so previous-invocation cwd deletion doesn't break us.
            string savedCwd;
            try { savedCwd = Directory.GetCurrentDirectory(); }
            catch { savedCwd = _stableHomeDir; }
            var savedOut = Console.Out;
            var savedErr = Console.Error;
            try {
                if (!string.IsNullOrEmpty(req.Cwd) && Directory.Exists(req.Cwd))
                    Directory.SetCurrentDirectory(req.Cwd);
                else if (!Directory.Exists(savedCwd))
                    try { Directory.SetCurrentDirectory(_stableHomeDir); } catch { }
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                return TaskExecutor.Execute(req);
            } finally {
                Console.SetOut(savedOut);
                Console.SetError(savedErr);
                // Always restore to a stable cwd: the saved cwd may have been deleted
                // by the client between invocations (common in test harnesses).
                try {
                    if (Directory.Exists(savedCwd))
                        Directory.SetCurrentDirectory(savedCwd);
                    else
                        Directory.SetCurrentDirectory(_stableHomeDir);
                } catch { }
            }
        } finally {
            ExecutionLock.Release();
        }
    }

    static async Task MonitorIdleAsync(CancellationTokenSource cts) {
        var idle = TimeSpan.FromMinutes(IdleMinutes);
        while (!cts.IsCancellationRequested) {
            try { await Task.Delay(TimeSpan.FromSeconds(30), cts.Token); }
            catch (OperationCanceledException) { return; }
            if (_activeConnections > 0) continue;
            if (DateTime.UtcNow - _lastActivityUtc < idle) continue;
            Log.WriteLine($"idle for {IdleMinutes}m — shutting down");
            cts.Cancel();
            return;
        }
    }

    static int Fatal(string message) {
        Console.Error.WriteLine($"bsharp-taskd: {message}");
        return 2;
    }

    static void DetachFromSpawner() {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // Create a new session so the daemon survives when the spawning shell
        // exits (or signals its process group). Returning -1 here just means we
        // were already a session leader — harmless.
        _ = setsid();
        // Redirect stdin to /dev/null so we never block on (or hold open) the
        // launcher's stdin handle.
        var devnull = open("/dev/null", O_RDONLY, 0);
        if (devnull >= 0) {
            _ = dup2(devnull, 0);
            if (devnull != 0) _ = close(devnull);
        }
        // Conservatively close descriptors 3..1023. macOS' default soft
        // RLIMIT_NOFILE is 256, hard is normally 1024 — covering 1024 is cheap
        // and safe. fds 1 and 2 stay attached to whatever the spawner gave us
        // until we re-point them at the daemon log a few statements later.
        for (var fd = 3; fd < 1024; fd++) {
            _ = close(fd);
        }
    }
}
