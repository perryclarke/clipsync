using System;
using System.Collections.Generic;
using System.Linq;
using ClipSync.Net;
using ClipSync.Settings;
using ClipSync.Ui;
using Xunit;

namespace ClipSync.Linux.Tests;

/// The tray menu's shape and wording, checked without a bus. Since the app
/// window became the primary UI this menu is the right-click fallback and
/// is deliberately three items: Open, Pause, Quit. The D-Bus marshalling
/// around it is verified by calling GetLayout on the running daemon.
public class TrayMenuTests
{
    private static TrayMenu Build(bool paused = false,
                                  Action<bool>? setPaused = null,
                                  Action? openWindow = null,
                                  Action? quit = null,
                                  IReadOnlyList<Peer>? peers = null,
                                  IReadOnlyList<HiddenPeer>? hidden = null)
    {
        var menu = new TrayMenu();
        menu.Build(
            new TrayState(peers ?? Array.Empty<Peer>(), paused, _ => false,
                          hidden ?? Array.Empty<HiddenPeer>(), Array.Empty<AppIdentity>()),
            new TrayActions(
                SetPaused: setPaused ?? (_ => { }),
                Trust: _ => { },
                SetMuted: (_, _) => { },
                Hide: (_, _) => { },
                Unhide: _ => { },
                OpenWindow: openWindow ?? (() => { }),
                Quit: quit ?? (() => { }),
                AddExclusion: _ => { },
                RemoveExclusion: _ => { },
                SetStartAtLogin: _ => { },
                StartOver: () => { },
                SetDebugLogging: _ => { },
                CopyLogCommand: () => { }));
        return menu;
    }

    private static IEnumerable<string> Labels(TrayMenu menu)
        => menu.All().Where(i => !i.IsSeparator && i.Label.Length > 0)
                     .Select(i => i.Label);

    /// The window is where devices live now; the menu only opens it,
    /// pauses, and quits. Anything more belongs in the window — dbusmenu
    /// renders as a flat list that closes on every click.
    [Fact]
    public void MenuIsExactlySettingsPauseQuit()
        => Assert.Equal(new[] { "Settings…", "Pause Syncing", "Quit ClipSync" },
                        Labels(Build()));

    [Fact]
    public void PauseEntry_TracksState()
    {
        Assert.Contains("Pause Syncing", Labels(Build()));
        Assert.Contains("Resume Syncing", Labels(Build(paused: true)));
    }

    [Fact]
    public void ClickingPause_InvokesTheCallbackWithTheOppositeState()
    {
        bool? requested = null;
        var menu = Build(paused: false, setPaused: p => requested = p);

        menu.All().First(i => i.Label == "Pause Syncing").Activate!();

        Assert.True(requested);
    }

    /// Settings is first: it is the entry a host that never routes Activate
    /// leaves as the only way into the window.
    [Fact]
    public void SettingsIsFirst_AndOpensTheWindow()
    {
        var opened = false;
        var menu = Build(openWindow: () => opened = true);

        var first = menu.Root.Children[0];
        Assert.Equal("Settings…", first.Label);
        first.Activate!();

        Assert.True(opened);
    }

    [Fact]
    public void QuitIsAlwaysLast()
    {
        var quitted = false;
        var menu = Build(quit: () => quitted = true);

        var last = menu.Root.Children[^1];
        Assert.Equal("Quit ClipSync", last.Label);
        last.Activate!();

        Assert.True(quitted);
    }

    /// Ids are what come back in a dbusmenu Event, so every item needs a
    /// distinct one and Find must resolve it.
    [Fact]
    public void EveryItemHasAUniqueResolvableId()
    {
        var menu = Build();
        var ids = menu.All().Select(i => i.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.NotNull(menu.Find(id)));
    }

    [Fact]
    public void RootHasIdZero_WhichHostsAskForByConvention()
        => Assert.Equal(0, Build().Root.Id);

    // ---- Attention -------------------------------------------------
    //
    // A device waiting to be trusted is the one state that needs the user to
    // act, and the tray icon is where it has to show: a peer refused at the
    // TLS handshake is otherwise invisible until they go looking.

    [Fact]
    public void NoPeersNeedsNoAttention()
    {
        Assert.False(Build().NeedsAttention);
    }

    private static Peer Knocker(string did = "aabbccdd")
        => new(did, did, PeerState.Pending, Version: null, Knocked: true);

    [Fact]
    public void PeerThatKnockedNeedsAttention()
    {
        Assert.True(Build(peers: new[] { Knocker() }).NeedsAttention);
    }

    /// The whole point of the Knocked flag: a device merely seen advertising
    /// is not asking this user for anything, and on a shared network badging
    /// for every stranger's ClipSync would mean a badge that never clears.
    /// It still gets a row and a Trust button — it just does not nag.
    [Fact]
    public void MerelyDiscoveredPeerNeedsNoAttention()
    {
        var menu = Build(peers: new[] { new Peer("aabbccdd", "Someone's laptop", PeerState.Pending) });
        Assert.False(menu.NeedsAttention);
    }

    [Theory]
    [InlineData(PeerState.Online)]
    [InlineData(PeerState.Looking)]
    [InlineData(PeerState.Offline)]
    public void OtherStatesNeedNoAttention(PeerState state)
    {
        var menu = Build(peers: new[] { new Peer("aabbccdd", "Peer", state, Knocked: true) });
        Assert.False(menu.NeedsAttention);
    }

    /// Hiding a device is a display preference, so a hidden one must not
    /// badge the tray with something the user cannot see or act on.
    [Fact]
    public void HiddenKnockerNeedsNoAttention()
    {
        var menu = Build(peers: new[] { Knocker() },
                         hidden: new[] { new HiddenPeer("AABBCCDD", "Old laptop") });
        Assert.False(menu.NeedsAttention);
    }

    /// Pausing does not make a device asking to be trusted go away.
    [Fact]
    public void PausedStillNeedsAttentionWhenAPeerKnocked()
    {
        var menu = Build(paused: true, peers: new[] { Knocker() });
        Assert.True(menu.NeedsAttention);
        Assert.True(menu.Paused);
    }

    /// One quiet stranger must not mask a device that really is asking.
    [Fact]
    public void AKnockerAmongDiscoveredPeersStillNeedsAttention()
    {
        var menu = Build(peers: new[]
        {
            new Peer("11112222", "Someone's laptop", PeerState.Pending),
            Knocker("aabbccdd"),
        });
        Assert.True(menu.NeedsAttention);
    }
}
