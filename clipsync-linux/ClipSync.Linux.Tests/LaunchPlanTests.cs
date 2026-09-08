using ClipSync.Platform;
using Xunit;

namespace ClipSync.Linux.Tests;

/// Where a shell-launched daemon goes. Running `clipsync` from a prompt
/// should hand the prompt back, but the same binary is also what systemd
/// and the XDG autostart entry exec — and a Type=simple unit whose process
/// forks and exits looks to systemd like a service that died. So the
/// decision keys off whether anyone is actually holding a terminal, and
/// the one-shot commands that print and exit are never backgrounded.
public class LaunchPlanTests
{
    [Fact]
    public void FromATerminal_BackgroundsByDefault()
        => Assert.Equal(LaunchMode.Background,
                        LaunchPlan.Decide([], stdinIsTerminal: true));

    [Fact]
    public void WithNoTerminal_StaysInForeground()
        => Assert.Equal(LaunchMode.Foreground,
                        LaunchPlan.Decide([], stdinIsTerminal: false));

    [Theory]
    [InlineData("--foreground")]
    [InlineData("-f")]
    public void TheForegroundFlag_Wins(string flag)
        => Assert.Equal(LaunchMode.Foreground,
                        LaunchPlan.Decide([flag], stdinIsTerminal: true));

    /// These print and exit. Backgrounding them would send the output the
    /// caller asked for to a log file and hand back an empty prompt.
    [Theory]
    [InlineData("--self-test")]
    [InlineData("--identity-only")]
    public void OneShotCommands_StayInForeground(string flag)
        => Assert.Equal(LaunchMode.Foreground,
                        LaunchPlan.Decide([flag], stdinIsTerminal: true));

    /// --debug only opens the log sink; it says nothing about where the
    /// daemon should run. The backgrounded copy writes it to the log file.
    [Fact]
    public void DebugAlone_StillBackgrounds()
        => Assert.Equal(LaunchMode.Background,
                        LaunchPlan.Decide(["--debug"], stdinIsTerminal: true));

    /// The guard has to skip these too: --self-test and --identity-only are
    /// diagnostics you run *while* a daemon is up, so refusing them because
    /// one is running would break the case they exist for.
    [Theory]
    [InlineData("--self-test")]
    [InlineData("--identity-only")]
    public void OneShotCommands_AreRecognised(string flag)
        => Assert.True(LaunchPlan.IsOneShot([flag]));

    [Fact]
    public void APlainLaunch_IsNotOneShot()
        => Assert.False(LaunchPlan.IsOneShot(["--debug"]));

    /// The child is re-exec'd, not forked, so it has to be told not to
    /// background itself again.
    [Fact]
    public void ChildArgs_AddTheForegroundFlag()
        => Assert.Equal(["--debug", "--foreground"], LaunchPlan.ChildArgs(["--debug"]));

    [Fact]
    public void ChildArgs_DoNotRepeatTheFlag()
        => Assert.Equal(["--foreground"], LaunchPlan.ChildArgs(["--foreground"]));
}
