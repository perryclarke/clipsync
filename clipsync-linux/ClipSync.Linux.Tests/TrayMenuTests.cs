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
                                  Action? quit = null)
    {
        var menu = new TrayMenu();
        menu.Build(
            new TrayState(Array.Empty<Peer>(), paused, _ => false,
                          Array.Empty<HiddenPeer>()),
            new TrayActions(
                SetPaused: setPaused ?? (_ => { }),
                Trust: _ => { },
                SetMuted: (_, _) => { },
                Hide: (_, _) => { },
                Unhide: _ => { },
                OpenWindow: openWindow ?? (() => { }),
                Quit: quit ?? (() => { })));
        return menu;
    }

    private static IEnumerable<string> Labels(TrayMenu menu)
        => menu.All().Where(i => !i.IsSeparator && i.Label.Length > 0)
                     .Select(i => i.Label);

    /// The window is where devices live now; the menu only opens it,
    /// pauses, and quits. Anything more belongs in the window — dbusmenu
    /// renders as a flat list that closes on every click.
    [Fact]
    public void MenuIsExactlyOpenPauseQuit()
        => Assert.Equal(new[] { "Open ClipSync", "Pause Syncing", "Quit ClipSync" },
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

    /// Open is first: it is the entry a host that never routes Activate
    /// leaves as the only way into the window.
    [Fact]
    public void OpenIsFirst_AndOpensTheWindow()
    {
        var opened = false;
        var menu = Build(openWindow: () => opened = true);

        var first = menu.Root.Children[0];
        Assert.Equal("Open ClipSync", first.Label);
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
}
