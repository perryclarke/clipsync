using System;
using System.IO;
using ClipSync.Clipboard;
using ClipSync.Settings;
using Xunit;

namespace ClipSync.Tests;

/// The assertion the excluded-apps feature exists to guarantee: an item
/// copied while an excluded app was in front is not transmitted, and
/// everything else is.
///
/// The foreground side is driven through a real `ForegroundRing` — the same
/// object `ForegroundTracker` delegates `AppAt` to — so no window is
/// touched. One test additionally drives `ForegroundTracker` itself through
/// the `IWindowResolver` seam to prove the production wiring resolves.
public class SuppressionPolicyTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly AppSettings _settings;

    public SuppressionPolicyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "clipsync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settings = AppSettings.Load(Path.Combine(_dir, "settings.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AppIdentity Exe(string name, string? path = null) =>
        new(AppKind.Exe, path ?? name + ".exe", name, path);

    [Fact]
    public void UnresolvedSourceAppTransmits()
    {
        // Fail open: nothing recorded, so the ring cannot say what was in
        // front. The item must still go out.
        _settings.Add(Exe("keepassxc"));
        var ring = new ForegroundRing();

        Assert.False(SuppressionPolicy.ShouldSuppress(ring, _settings, T0, out var source));
        Assert.Null(source);
    }

    [Fact]
    public void CopyBeforeTheOldestRetainedEntryTransmits()
    {
        // Same fail-open rule, reached the other way: the ring knows about
        // later transitions but nothing covering the moment of the copy.
        _settings.Add(Exe("keepassxc"));
        var ring = new ForegroundRing();
        ring.Record(T0, Exe("keepassxc"));

        Assert.False(SuppressionPolicy.ShouldSuppress(ring, _settings, T0.AddSeconds(-1), out var source));
        Assert.Null(source);
    }

    [Fact]
    public void ResolvedButNotExcludedSourceTransmits()
    {
        _settings.Add(Exe("keepassxc"));
        var ring = new ForegroundRing();
        ring.Record(T0, Exe("notepad"));

        Assert.False(SuppressionPolicy.ShouldSuppress(ring, _settings, T0.AddSeconds(1), out var source));
        Assert.Equal(Exe("notepad"), source);
    }

    [Fact]
    public void ExcludedSourceIsSuppressed()
    {
        _settings.Add(Exe("keepassxc"));
        var ring = new ForegroundRing();
        ring.Record(T0, Exe("keepassxc"));

        Assert.True(SuppressionPolicy.ShouldSuppress(ring, _settings, T0.AddSeconds(1), out var source));
        Assert.Equal(Exe("keepassxc"), source);
    }

    [Fact]
    public void ExclusionMatchesAcrossAVersionedInstallDirectory()
    {
        // The reason the key is the bare file name: an auto-update that moves
        // the exe must not silently un-exclude the app.
        _settings.Add(Exe("Discord", @"C:\Users\me\AppData\Local\Discord\app-1.0.9\Discord.exe"));
        var ring = new ForegroundRing();
        ring.Record(T0, Exe("Discord", @"C:\Users\me\AppData\Local\Discord\app-1.0.10\Discord.exe"));

        Assert.True(SuppressionPolicy.ShouldSuppress(ring, _settings, T0.AddSeconds(1), out _));
    }

    [Fact]
    public void ExcludingAnExeDoesNotExcludeAPackageOfTheSameName()
    {
        _settings.Add(new AppIdentity(AppKind.Exe, "terminal.exe", "Terminal"));
        var ring = new ForegroundRing();
        ring.Record(T0, new AppIdentity(AppKind.Package, "terminal.exe", "Terminal"));

        Assert.False(SuppressionPolicy.ShouldSuppress(ring, _settings, T0.AddSeconds(1), out _));
    }

    [Fact]
    public void ExcludedPackageIsSuppressed()
    {
        var terminal = new AppIdentity(AppKind.Package, "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "Windows Terminal");
        _settings.Add(terminal);
        var ring = new ForegroundRing();
        ring.Record(T0, new AppIdentity(AppKind.Package, "microsoft.windowsterminal_8wekyb3d8bbwe", "Terminal"));

        Assert.True(SuppressionPolicy.ShouldSuppress(ring, _settings, T0.AddSeconds(1), out _));
    }

    [Fact]
    public void AForegroundSourceThatThrowsTransmits()
    {
        _settings.Add(Exe("keepassxc"));

        Assert.False(SuppressionPolicy.ShouldSuppress(new ThrowingSource(), _settings, T0, out var source));
        Assert.Null(source);
    }

    [Fact]
    public void ExclusionStopsApplyingAsSoonAsItIsRemoved()
    {
        // No restart: the settings object the watcher holds is the live one.
        var app = Exe("keepassxc");
        _settings.Add(app);
        var ring = new ForegroundRing();
        ring.Record(T0, app);

        Assert.True(SuppressionPolicy.ShouldSuppress(ring, _settings, T0.AddSeconds(1), out _));
        _settings.Remove(app);
        Assert.False(SuppressionPolicy.ShouldSuppress(ring, _settings, T0.AddSeconds(1), out _));
    }

    [Fact]
    public void TrackerSeededThroughTheResolverSeamFeedsTheDecision()
    {
        // The production object, driven synthetically: Start() seeds the ring
        // from IWindowResolver, which is the same path a real focus change
        // takes. Proves the tracker actually satisfies IForegroundSource for
        // the policy, not just the ring underneath it.
        var app = Exe("keepassxc");
        _settings.Add(app);

        using var tracker = new ForegroundTracker(new FakeResolver(app));
        tracker.Start();
        try
        {
            Assert.Equal(app, tracker.Current);
            Assert.True(SuppressionPolicy.ShouldSuppress(tracker, _settings, DateTime.UtcNow, out var source));
            Assert.Equal(app, source);
        }
        finally { tracker.Stop(); }
    }

    [Fact]
    public void TrackerThatCannotResolveTheSeedTransmits()
    {
        _settings.Add(Exe("keepassxc"));

        using var tracker = new ForegroundTracker(new FakeResolver(null));
        tracker.Start();
        try
        {
            Assert.Null(tracker.Current);
            Assert.False(SuppressionPolicy.ShouldSuppress(tracker, _settings, DateTime.UtcNow, out _));
        }
        finally { tracker.Stop(); }
    }

    private sealed class FakeResolver : IWindowResolver
    {
        private readonly AppIdentity? _app;
        public FakeResolver(AppIdentity? app) => _app = app;
        public IntPtr GetForegroundWindow() => new(1);
        public AppIdentity? Resolve(IntPtr hwnd) => _app;
    }

    private sealed class ThrowingSource : IForegroundSource
    {
        public AppIdentity? AppAt(DateTime utc) => throw new InvalidOperationException("boom");
    }
}
