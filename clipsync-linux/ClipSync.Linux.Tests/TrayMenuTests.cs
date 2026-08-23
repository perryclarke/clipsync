using System;
using System.Collections.Generic;
using System.Linq;
using ClipSync.Net;
using ClipSync.Settings;
using ClipSync.Ui;
using Xunit;

namespace ClipSync.Linux.Tests;

/// The tray menu's shape and wording, checked without a bus. The D-Bus
/// marshalling around it is verified by calling GetLayout on the running
/// daemon; what matters here is that the right entries exist and do the
/// right thing.
public class TrayMenuTests
{
    private static Peer Online(string did, string name, string? version = "0.8.0")
        => new(did, name, PeerState.Online, version);

    private static Peer Pending(string did, string name)
        => new(did, name, PeerState.Pending);

    private static TrayMenu Build(IReadOnlyList<Peer> peers, bool paused = false,
                                  Func<string, bool>? isMuted = null,
                                  Action<bool>? setPaused = null,
                                  Action<string>? trust = null,
                                  Action<string, bool>? setMuted = null,
                                  Action? quit = null,
                                  IReadOnlyList<HiddenPeer>? hidden = null,
                                  Action<string, string>? hide = null,
                                  Action<string>? unhide = null,
                                  string? expand = null)
    {
        var menu = new TrayMenu();
        var state = new TrayState(peers, paused, isMuted ?? (_ => false),
                                  hidden ?? Array.Empty<HiddenPeer>());
        var actions = new TrayActions(
            SetPaused: setPaused ?? (_ => { }),
            Trust: trust ?? (_ => { }),
            SetMuted: setMuted ?? ((_, _) => { }),
            Hide: hide ?? ((_, _) => { }),
            Unhide: unhide ?? (_ => { }),
            Quit: quit ?? (() => { }));

        menu.Build(state, actions);

        // `expand` is retained so the tests read as "this device's actions",
        // but nothing is disclosed any more: every action is always shown.
        return menu;
    }

    private static TrayMenu BuildLegacy(IReadOnlyList<Peer> peers, bool paused,
                                        Func<string, bool>? isMuted,
                                        Action<bool>? setPaused,
                                        Action<string>? trust,
                                        Action<string, bool>? setMuted,
                                        Action? quit,
                                        IReadOnlyList<HiddenPeer>? hidden,
                                        Action<string, string>? hide,
                                        Action<string>? unhide)
    {
        var menu = new TrayMenu();
        menu.Build(
            new TrayState(peers, paused, isMuted ?? (_ => false),
                          hidden ?? Array.Empty<HiddenPeer>()),
            new TrayActions(
                SetPaused: setPaused ?? (_ => { }),
                Trust: trust ?? (_ => { }),
                SetMuted: setMuted ?? ((_, _) => { }),
                Hide: hide ?? ((_, _) => { }),
                Unhide: unhide ?? (_ => { }),
                Quit: quit ?? (() => { })));
        return menu;
    }

    /// Action labels are indented to show they belong to the device above,
    /// so comparisons here trim rather than encode the padding.
    private static IEnumerable<string> Labels(TrayMenu menu)
        => menu.All().Where(i => !i.IsSeparator && i.Label.Length > 0)
                     .Select(i => i.Label.Trim());

    [Fact]
    public void PauseEntry_TracksState()
    {
        Assert.Contains("Pause Syncing", Labels(Build([])));
        Assert.Contains("Resume Syncing", Labels(Build([], paused: true)));
    }

    /// The fingerprint is on the Trust entry because eyeballing it against
    /// the other device is the only verification two-sided TOFU offers.
    [Fact]
    public void PendingPeer_OffersTrustWithItsFingerprint()
    {
        var did = "e8719325c1adbbb5ccc203cd23aa1482";
        var menu = Build([Pending(did, "Kadobe M1 MBP8")], expand: did);

        Assert.Contains(Labels(menu), l => l.EndsWith("Kadobe M1 MBP8 — Waiting to be trusted"));
        Assert.Contains("Trust this device (e8719325)", Labels(menu));
    }

    [Fact]
    public void OnlinePeer_ShowsVersionAndOffersPerPeerPause()
    {
        var did = "b6bf89d94fc27ef9a4b7d0da5f8fae81";
        var menu = Build([Online(did, "Kodachrome")], expand: did);

        Assert.Contains(Labels(menu), l => l.EndsWith("Kodachrome — Online, 0.8.0"));
        Assert.Contains("Pause sending to this device", Labels(menu));
        Assert.DoesNotContain(Labels(menu), l => l.StartsWith("Trust"));
    }

    [Fact]
    public void MutedPeer_OffersResumeInstead()
    {
        var menu = Build([Online("b6bf89d9", "Kodachrome")], isMuted: _ => true,
                         expand: "b6bf89d9");
        Assert.Contains("Resume sending to this device", Labels(menu));
    }

    [Fact]
    public void EmptyPeerList_SaysSoRatherThanShowingNothing()
    {
        var menu = Build([]);
        var empty = Assert.Single(menu.All(), i => i.Label == "No devices found");
        Assert.False(empty.Enabled);
    }

    [Fact]
    public void ClickingPause_InvokesTheCallbackWithTheOppositeState()
    {
        bool? requested = null;
        var menu = Build([], paused: false, setPaused: p => requested = p);

        menu.All().First(i => i.Label.Trim() == "Pause Syncing").Activate!();

        Assert.True(requested);
    }

    [Fact]
    public void ClickingTrust_PassesThePeersFullDid()
    {
        string? trusted = null;
        var did = "e8719325c1adbbb5ccc203cd23aa1482";
        var menu = Build([Pending(did, "Kadobe")], trust: d => trusted = d, expand: did);

        menu.All().First(i => i.Label.Trim().StartsWith("Trust")).Activate!();

        Assert.Equal(did, trusted);
    }

    [Fact]
    public void ClickingPerPeerPause_PassesDidAndNewState()
    {
        (string Did, bool Muted)? call = null;
        var did = "b6bf89d94fc27ef9a4b7d0da5f8fae81";
        var menu = Build([Online(did, "Kodachrome")], setMuted: (d, m) => call = (d, m), expand: did);

        menu.All().First(i => i.Label.Trim() == "Pause sending to this device").Activate!();

        Assert.Equal((did, true), call);
    }

    /// Ids are what come back in a dbusmenu Event, so every item needs a
    /// distinct one and Find must resolve it.
    [Fact]
    public void EveryItemHasAUniqueResolvableId()
    {
        var menu = Build([Online("aaaa1111", "A"), Pending("bbbb2222", "B")]);
        var ids = menu.All().Select(i => i.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.NotNull(menu.Find(id)));
    }

    [Fact]
    public void QuitIsAlwaysLast()
        => Assert.Equal("Quit ClipSync", Build([Online("aaaa1111", "A")]).Root.Children[^1].Label);

    /// Rebuilding renumbers, which is why the host is told to re-read the
    /// layout. What must not happen is a stale id resolving to the wrong
    /// item after peers change.
    [Fact]
    public void Rebuilding_DoesNotLeaveStaleIdsBehind()
    {
        var menu = new TrayMenu();
        var actions = new TrayActions(_ => { }, _ => { }, (_, _) => { },
                                      (_, _) => { }, _ => { }, () => { });
        menu.Build(new TrayState([Online("aaaa1111", "A"), Online("bbbb2222", "B")],
                                 false, _ => false, []), actions);
        var highest = menu.All().Max(i => i.Id);

        menu.Build(new TrayState([], false, _ => false, []), actions);

        Assert.Null(menu.Find(highest));
    }

    /// Hiding is a display preference: the device stays discovered and
    /// connected, it just stops being listed.
    [Fact]
    public void HiddenDevice_DisappearsFromTheListButIsShownAsHidden()
    {
        var did = "e8719325c1adbbb5ccc203cd23aa1482";
        var menu = Build([Pending(did, "Kadobe M1 MBP8")],
                         hidden: [new HiddenPeer(did, "Kadobe M1 MBP8")]);

        Assert.DoesNotContain(Labels(menu), l => l.EndsWith("Kadobe M1 MBP8 — Waiting to be trusted"));
        Assert.Contains("Hidden devices (1)", Labels(menu));
        Assert.Contains("Show Kadobe M1 MBP8", Labels(menu));
    }

    [Fact]
    public void PendingPeer_CanBeHidden()
    {
        (string Did, string Name)? call = null;
        var did = "e8719325c1adbbb5ccc203cd23aa1482";
        var menu = Build([Pending(did, "Kadobe")], hide: (d, n) => call = (d, n), expand: did);

        menu.All().First(i => i.Label.Trim() == "Hide this device").Activate!();

        Assert.Equal((did, "Kadobe"), call);
    }

    [Fact]
    public void ShowEntry_UnhidesThatDevice()
    {
        string? unhidden = null;
        var did = "e8719325c1adbbb5ccc203cd23aa1482";
        var menu = Build([], hidden: [new HiddenPeer(did, "Kadobe")], unhide: d => unhidden = d);

        menu.All().First(i => i.Label.Trim().StartsWith("Show ")).Activate!();

        Assert.Equal(did, unhidden);
    }

    /// The whole reason the menu is flat: nested submenus render as an
    /// arrow that opens nothing under GNOME's AppIndicator extension.
    [Fact]
    public void MenuIsFlat_NoItemHasChildrenExceptTheRoot()
    {
        var menu = Build([Online("aaaa1111", "A"), Pending("bbbb2222", "B")], expand: "aaaa1111");

        Assert.All(menu.Root.Children, child => Assert.Empty(child.Children));
    }




}
