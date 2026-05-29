// Shared helpers for resolving the bsharp-taskd socket / lock / pid file paths.
// Both the daemon and the generated host derive the same paths from the same
// (uid, daemonVersion, sdkFingerprint) tuple so they can find each other without
// configuration. The directory is per-user with 0700 permissions to prevent
// other users on a shared machine from connecting.
#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Bsharp.Generated.TaskModel;

public static class DaemonPaths {
    public const string DaemonVersion = "1";

    [DllImport("libc", EntryPoint = "getuid", SetLastError = false)]
    static extern uint LibcGetuid();

    public static string GetUserDir() {
        var tmp = Path.GetTempPath();
        var uid = GetCurrentUserId();
        var dir = Path.Combine(tmp, $"bsharp-{uid}");
        try {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                try {
                    File.SetUnixFileMode(dir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                } catch { }
            }
        } catch { }
        return dir;
    }

    public static string GetSocketPath(string sdkFingerprint) =>
        Path.Combine(GetUserDir(), $"taskd-{DaemonVersion}-{sdkFingerprint}.sock");

    public static string GetLockPath(string sdkFingerprint) =>
        Path.Combine(GetUserDir(), $"taskd-{DaemonVersion}-{sdkFingerprint}.lock");

    public static string GetPidPath(string sdkFingerprint) =>
        Path.Combine(GetUserDir(), $"taskd-{DaemonVersion}-{sdkFingerprint}.pid");

    public static string GetLogPath(string sdkFingerprint) =>
        Path.Combine(GetUserDir(), $"taskd-{DaemonVersion}-{sdkFingerprint}.log");

    static string GetCurrentUserId() {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Environment.UserName;
        try { return LibcGetuid().ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch { return Environment.UserName; }
    }
}
