using System.Collections.Generic;
using System.Linq;

namespace ClipSync.Platform;

internal enum LaunchMode
{
    /// Re-exec detached and hand the prompt back.
    Background,

    /// Run here, in this process.
    Foreground,
}

/// Decides where a launch of the daemon should run. Pure so the rule can
/// be tested without a terminal, a display, or a session bus.
internal static class LaunchPlan
{
    public const string ForegroundFlag = "--foreground";
    public const string ForegroundShortFlag = "-f";

    /// Print-and-exit commands. Backgrounding one would redirect the very
    /// output the caller asked for into the log file.
    private static readonly string[] OneShot = ["--self-test", "--identity-only"];

    /// Diagnostics that print and exit rather than starting a daemon.
    public static bool IsOneShot(IReadOnlyList<string> args) => args.Any(OneShot.Contains);

    public static LaunchMode Decide(IReadOnlyList<string> args, bool stdinIsTerminal)
    {
        if (args.Contains(ForegroundFlag) || args.Contains(ForegroundShortFlag))
            return LaunchMode.Foreground;
        if (IsOneShot(args))
            return LaunchMode.Foreground;

        // No terminal means nothing to detach from: systemd's Type=simple
        // unit and the XDG autostart entry both land here, and both need
        // the process they started to be the process that stays.
        return stdinIsTerminal ? LaunchMode.Background : LaunchMode.Foreground;
    }

    /// The argument list for the re-exec'd copy. It is a fresh process, not
    /// a fork, so the flag is how it learns not to background in turn.
    public static string[] ChildArgs(IReadOnlyList<string> args)
        => args.Contains(ForegroundFlag) || args.Contains(ForegroundShortFlag)
            ? [.. args]
            : [.. args, ForegroundFlag];
}
