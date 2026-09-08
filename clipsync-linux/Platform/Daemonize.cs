using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ClipSync.Platform.Backends;

namespace ClipSync.Platform;

/// Backgrounding, by re-exec rather than fork.
///
/// fork() in a running CLR is not safe — the runtime has threads, and only
/// the calling one survives into the child — so the parent starts a fresh
/// copy of itself and exits. The child does its own detaching: it is the
/// one that has to outlive the terminal.
internal static class Daemonize
{
    private const int O_RDONLY = 0x0;
    private const int O_WRONLY = 0x1;
    private const int O_CREAT = 0x40;
    private const int O_TRUNC = 0x200;

    /// Set on the child so it knows to detach. The foreground flag alone
    /// cannot say this: `clipsync --foreground` typed at a prompt is meant
    /// to stay attached.
    private const string SpawnedMarker = "CLIPSYNC_DAEMONIZED";

    [DllImport("libc", SetLastError = true)] private static extern int setsid();
    [DllImport("libc", SetLastError = true)] private static extern int dup2(int oldfd, int newfd);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags, int mode);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

    /// Truncated per launch rather than appended: under systemd the journal
    /// already keeps history, so this file only ever needs to explain the
    /// copy that is running now, and nothing else rotates it.
    public static string LogPath
        => Path.Combine(FilePermissionStore.DefaultDirectory, "clipsync.log");

    public static bool WasSpawned
        => Environment.GetEnvironmentVariable(SpawnedMarker) == "1";

    /// Start the detached copy and return its pid, or null if it could not
    /// be started.
    public static int? Spawn(IReadOnlyList<string> args)
    {
        if (Environment.ProcessPath is not { } exe) return null;

        // Detach before exec, not after. Calling setsid() from inside the
        // child is too late: the CLR takes long enough to start that the
        // terminal is usually gone — and its SIGHUP delivered — before Main
        // runs, so the daemon dies a few hundred milliseconds after the
        // prompt comes back. setsid(1) makes the new session first and then
        // execs, which closes the window entirely.
        //
        // It execs in place rather than forking when the caller is not a
        // process group leader, which is the case here, so the pid this
        // returns is the daemon's own.
        var setsid = new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);

        var info = new ProcessStartInfo(setsid ?? exe) { UseShellExecute = false };
        if (setsid is not null) info.ArgumentList.Add(exe);
        foreach (var a in LaunchPlan.ChildArgs(args)) info.ArgumentList.Add(a);
        info.Environment[SpawnedMarker] = "1";

        // No redirects here on purpose: piping the child's output through
        // this process would mean this process has to stay alive to pump
        // it, which is the opposite of the point. The child reopens its own
        // descriptors onto the log instead.
        return Process.Start(info)?.Id;
    }

    /// Called by the spawned child before anything writes to the console.
    ///
    /// setsid() puts it in its own session, so closing the terminal does not
    /// SIGHUP it. Reopening the descriptors rather than swapping Console's
    /// TextWriters matters because GLib and the X libraries write to fd 2
    /// directly — Console.SetError would silently drop exactly the output
    /// worth having when the display is the problem.
    public static void DetachFromTerminal()
    {
        // Harmless EPERM when Spawn already routed through setsid(1); this
        // is the path that matters when it could not.
        setsid();

        Directory.CreateDirectory(FilePermissionStore.DefaultDirectory);
        var log = open(LogPath, O_WRONLY | O_CREAT | O_TRUNC, 0x180 /* 0600 */);
        if (log >= 0)
        {
            dup2(log, 1);
            dup2(log, 2);
            if (log > 2) close(log);
        }

        // stdin becomes /dev/null, which is also what tells the rest of the
        // daemon there is no console to read commands from.
        var devnull = open("/dev/null", O_RDONLY, 0);
        if (devnull >= 0)
        {
            dup2(devnull, 0);
            if (devnull > 2) close(devnull);
        }
    }
}
