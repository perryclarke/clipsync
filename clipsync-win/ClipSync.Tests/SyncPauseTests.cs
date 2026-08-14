using System;
using System.IO;
using ClipSync.Settings;
using ClipSync.Sync;
using Xunit;

namespace ClipSync.Tests;

public class SyncPauseTests : IDisposable
{
    private const string Kodachrome = "b6bf89d94fc27ef9a4b7d0da5f8fae81342bb27e542ed64c545b1401744b1e83";
    private const string Other = "0dc5a1f66967f4ca4ed60e369347d66865f6877cae4f21307160c0bb0275954e";

    private readonly string _dir;
    private readonly string _path;

    public SyncPauseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "clipsync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SyncPause New() => new(AppSettings.Load(_path));

    [Fact]
    public void SendsToEveryoneByDefault()
    {
        var p = New();
        Assert.False(p.GlobalPaused);
        Assert.True(p.ShouldSendTo(Kodachrome));
        Assert.True(p.ShouldSendTo(Other));
    }

    [Fact]
    public void GlobalPauseStopsEveryPeer()
    {
        var p = New();
        p.GlobalPaused = true;
        Assert.False(p.ShouldSendTo(Kodachrome));
        Assert.False(p.ShouldSendTo(Other));
    }

    [Fact]
    public void MutingOnePeerLeavesTheOthersAlone()
    {
        var p = New();
        p.SetMuted(Kodachrome, true);
        Assert.False(p.ShouldSendTo(Kodachrome));
        Assert.True(p.ShouldSendTo(Other));
    }

    [Fact]
    public void ResumingAPeerWhileGloballyPausedStillSendsNothing()
    {
        // The two controls are independent gates, not one shared switch:
        // un-muting a peer must not quietly defeat the global pause.
        var p = New();
        p.GlobalPaused = true;
        p.SetMuted(Kodachrome, false);
        Assert.False(p.ShouldSendTo(Kodachrome));
    }

    [Fact]
    public void ResumingGloballyLeavesAMutedPeerMuted()
    {
        var p = New();
        p.SetMuted(Kodachrome, true);
        p.GlobalPaused = true;
        p.GlobalPaused = false;
        Assert.False(p.ShouldSendTo(Kodachrome));
        Assert.True(p.ShouldSendTo(Other));
    }

    [Fact]
    public void MuteIsCaseInsensitiveOnTheDid()
    {
        var p = New();
        p.SetMuted(Kodachrome.ToUpperInvariant(), true);
        Assert.False(p.ShouldSendTo(Kodachrome));
        Assert.True(p.IsMuted(Kodachrome.ToUpperInvariant()));
    }

    [Fact]
    public void MuteSurvivesAReload()
    {
        New().SetMuted(Kodachrome, true);

        var reloaded = New();
        Assert.True(reloaded.IsMuted(Kodachrome));
        Assert.False(reloaded.ShouldSendTo(Kodachrome));
    }

    [Fact]
    public void GlobalPauseDoesNotSurviveAReload()
    {
        // Deliberate: a global pause is "not right now", and a restart that
        // silently kept sync off would be a bad surprise.
        var p = New();
        p.GlobalPaused = true;

        Assert.False(New().GlobalPaused);
    }

    [Fact]
    public void UnmutingRemovesItFromDisk()
    {
        New().SetMuted(Kodachrome, true);
        New().SetMuted(Kodachrome, false);

        Assert.False(New().IsMuted(Kodachrome));
    }

    [Fact]
    public void MutingTwiceIsIdempotent()
    {
        var p = New();
        p.SetMuted(Kodachrome, true);
        p.SetMuted(Kodachrome, true);

        Assert.Single(New().MutedPeers);
    }

    [Fact]
    public void MutedPeersRoundTripAlongsideExcludedApps()
    {
        // One file holds both; neither may clobber the other.
        var settings = AppSettings.Load(_path);
        settings.Add(new AppIdentity(AppKind.Exe, @"C:\x\KeePassXC.exe", "KeePassXC"));
        new SyncPause(settings).SetMuted(Kodachrome, true);

        var reloaded = AppSettings.Load(_path);
        Assert.Single(reloaded.Excluded);
        Assert.True(new SyncPause(reloaded).IsMuted(Kodachrome));
    }

    [Fact]
    public void BlankOrMissingDidsAreIgnored()
    {
        var p = New();
        p.SetMuted("", true);
        p.SetMuted("   ", true);
        Assert.Empty(p.MutedPeers);
    }

    [Fact]
    public void CorruptFileLeavesNothingMuted()
    {
        File.WriteAllText(_path, "{ this is not json");
        Assert.Empty(New().MutedPeers);
    }
}
