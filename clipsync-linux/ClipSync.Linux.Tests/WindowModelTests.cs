using System;
using System.Collections.Generic;
using System.Linq;
using ClipSync.Net;
using ClipSync.Settings;
using ClipSync.Ui;
using Xunit;

namespace ClipSync.Linux.Tests;

/// The window's shape and wording, checked without GTK. MainWindow renders
/// exactly what WindowModel returns, so this is where regressions in the
/// device rows, their controls, and their words are caught.
public class WindowModelTests
{
    private static Peer Online(string did, string name, string? version = "0.8.0")
        => new(did, name, PeerState.Online, version);

    private static Peer Pending(string did, string name)
        => new(did, name, PeerState.Pending);

    private static WindowContent Build(IReadOnlyList<Peer> peers,
                                       bool paused = false,
                                       Func<string, bool>? isMuted = null,
                                       IReadOnlyList<HiddenPeer>? hidden = null,
                                       IReadOnlyList<AppIdentity>? excluded = null)
        => WindowModel.Build(
               new TrayState(peers, paused, isMuted ?? (_ => false),
                             hidden ?? Array.Empty<HiddenPeer>(),
                             excluded ?? Array.Empty<AppIdentity>()),
               "penguin", "0a1b2c3d");

    /// The fingerprint is in the pending subtitle because eyeballing it
    /// against the other device is the only verification two-sided TOFU
    /// offers.
    [Fact]
    public void PendingPeer_OffersTrustWithItsFingerprint()
    {
        var did = "e8719325c1adbbb5ccc203cd23aa1482";
        var row = Assert.Single(Build([Pending(did, "Kadobe M1 MBP8")]).Devices);

        Assert.True(row.OffersTrust);
        Assert.True(row.OffersHide);
        Assert.False(row.OffersSendSwitch);
        Assert.Equal("Kadobe M1 MBP8", row.Title);
        Assert.Equal("Waiting to be trusted — e8719325", row.Subtitle);
        Assert.Equal(did, row.Did);
    }

    [Fact]
    public void OnlinePeer_ShowsVersionAndGetsASendSwitch()
    {
        var row = Assert.Single(Build([Online("b6bf89d9", "Kodachrome")]).Devices);

        Assert.False(row.OffersTrust);
        // Unlike the other platforms, trusted devices can be hidden too.
        Assert.True(row.OffersHide);
        Assert.True(row.OffersSendSwitch);
        Assert.True(row.Sending);
        Assert.Equal("Online, 0.8.0", row.Subtitle);
    }

    /// A muted peer reads "Paused" rather than "Online": the reason
    /// nothing reaches it is the mute, not the network.
    [Fact]
    public void MutedPeer_ReadsPausedWithTheSwitchOff()
    {
        var row = Assert.Single(
            Build([Online("b6bf89d9", "Kodachrome")], isMuted: _ => true).Devices);

        Assert.False(row.Sending);
        Assert.Equal("Paused", row.Subtitle);
    }

    [Fact]
    public void OfflineAndLookingPeers_SaySo()
    {
        var content = Build([
            new Peer("aaaa1111", "A", PeerState.Offline),
            new Peer("bbbb2222", "B", PeerState.Looking),
        ]);

        Assert.Equal("Offline", content.Devices[0].Subtitle);
        Assert.Equal("Looking…", content.Devices[1].Subtitle);
    }

    [Fact]
    public void PauseStateAndThisDevice_PassThrough()
    {
        var content = Build([], paused: true);

        Assert.True(content.Paused);
        Assert.Equal("penguin", content.DeviceName);
        Assert.Equal("0a1b2c3d", content.Fingerprint);
    }

    /// Hiding is a display preference: the device stays discovered and
    /// connected, it just moves to the hidden list.
    [Fact]
    public void HiddenDevice_MovesToTheHiddenList()
    {
        var did = "e8719325c1adbbb5ccc203cd23aa1482";
        var content = Build([Pending(did, "Kadobe M1 MBP8")],
                            hidden: [new HiddenPeer(did, "Kadobe M1 MBP8")]);

        Assert.Empty(content.Devices);
        var hidden = Assert.Single(content.Hidden);
        Assert.Equal(did, hidden.Did);
        Assert.Equal("Kadobe M1 MBP8", hidden.Title);
    }

    [Fact]
    public void HiddenMatching_IsCaseInsensitive_LikeTheRestOfTheApp()
    {
        var did = "E8719325C1ADBBB5CCC203CD23AA1482";
        var content = Build([Pending(did.ToLowerInvariant(), "Kadobe")],
                            hidden: [new HiddenPeer(did, "Kadobe")]);

        Assert.Empty(content.Devices);
    }

    [Fact]
    public void EmptyPeerList_YieldsNoRows_ViewShowsTheEmptyLabel()
    {
        Assert.Empty(Build([]).Devices);
        Assert.Equal("No devices found", WindowModel.NoDevices);
    }

    [Fact]
    public void ExcludedApps_ListWithKeyAndDisplayName()
    {
        var content = Build([], excluded:
            [new AppIdentity(AppKind.Exe, "org.keepassxc.KeePassXC", "KeePassXC")]);

        var row = Assert.Single(content.Excluded);
        // AppIdentity lowercases Exe keys so matching is case-insensitive;
        // the display name keeps what the user typed.
        Assert.Equal("org.keepassxc.keepassxc", row.Key);
        Assert.Equal("KeePassXC", row.Title);
    }

    /// The command has to be the one that actually reads this daemon's
    /// output: it is copied verbatim and pasted into a terminal, so a stale
    /// unit name here is a dead end for whoever is debugging.
    [Fact]
    public void DebugSection_OffersTheJournalCommandForTheUserUnit()
    {
        Assert.Equal("journalctl --user -u clipsync -f", WindowModel.DebugLogCommand);
        Assert.Equal("Debug", WindowModel.DebugTitle);
        Assert.Equal("Debug logging", WindowModel.DebugLoggingTitle);
    }

    /// The promise that makes a log safe to hand to someone. If this stops
    /// being true, what gets logged needs re-checking, not just the wording.
    [Fact]
    public void DebugSection_PromisesMetadataOnly()
    {
        Assert.Contains("Never includes what you copied", WindowModel.DebugLoggingSubtitle);
        // No file to open on Linux — the journal is the log.
        Assert.Contains("journal", WindowModel.DebugLogSubtitle);
    }
}
