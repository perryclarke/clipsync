using System;
using System.Collections.Generic;
using System.Linq;
using ClipSync.Net;
using ClipSync.Settings;

namespace ClipSync.Ui;

/// One entry in the tray menu.
///
/// Ids are assigned when the menu is built and are what comes back in a
/// dbusmenu Event, so they must stay stable for as long as a menu snapshot
/// is live. Rebuilding renumbers, which is why a rebuild that changes
/// anything is paired with a LayoutUpdated signal.
internal sealed class MenuItem
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool IsSeparator { get; init; }

    /// Null for an ordinary item; true/false renders a checkmark.
    public bool? Checked { get; init; }

    public Action? Activate { get; init; }
    public List<MenuItem> Children { get; init; } = new();
}

/// What the menu shows.
internal sealed record TrayState(
    IReadOnlyList<Peer> Peers,
    bool Paused,
    Func<string, bool> IsMuted,
    IReadOnlyList<HiddenPeer> Hidden);

/// What the menu can do.
internal sealed record TrayActions(
    Action<bool> SetPaused,
    Action<string> Trust,
    Action<string, bool> SetMuted,
    Action<string, string> Hide,
    Action<string> Unhide,
    Action Quit);

/// Builds the tray menu.
///
/// **Flat, with every action visible.** Two richer shapes were tried against
/// GNOME's AppIndicator extension and neither works:
///
/// - *Real submenus.* The host fetches the whole tree, draws the submenu
///   arrow, and then never populates the child menu. Confirmed by serving
///   the full tree — GetLayout returns the grandchildren correctly — and
///   watching it still open empty.
/// - *Disclosure rows*, as the Quick Settings panel uses for Wi-Fi and Power
///   Mode. The extension makes every dbusmenu entry a PopupMenuItem, and
///   activating one closes the whole popup, so a row that expands on click
///   costs a reopen before the disclosed action can be picked. Two clicks
///   and a reopen to do what one click should.
///
/// So the menu shows everything at once: a device row, then its actions
/// indented beneath it. One click, one action, no reopening. Anything that
/// genuinely needs a richer interaction belongs in a settings window, not in
/// a tray menu the host can only render as a flat list.
internal sealed class TrayMenu
{
    private readonly Dictionary<int, MenuItem> _byId = new();
    private int _nextId;

    public MenuItem Root { get; private set; } = new();
    public bool Paused { get; private set; }

    public MenuItem? Find(int id) => _byId.TryGetValue(id, out var item) ? item : null;

    public void Build(TrayState state, TrayActions actions)
    {
        _byId.Clear();
        // The root is id 0 by convention, and hosts ask for it by that id:
        // GetLayout(0, ...) returning a root that calls itself something
        // else leaves the host with no menu at all — the icon appears and
        // clicking it does nothing.
        _nextId = 0;
        Paused = state.Paused;

        var root = New(new MenuItem());

        root.Children.Add(New(new MenuItem
        {
            Label = state.Paused ? "Resume Syncing" : "Pause Syncing",
            Checked = state.Paused,
            Activate = () => actions.SetPaused(!state.Paused),
        }));

        // Hidden devices are a display preference only: they stay
        // discovered, trusted and connected, they are just not listed.
        var hiddenDids = state.Hidden.Select(h => h.DidHex).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visible = state.Peers.Where(p => !hiddenDids.Contains(p.DidHex)).ToList();

        root.Children.Add(Separator());

        if (visible.Count == 0)
        {
            root.Children.Add(New(new MenuItem { Label = "No devices found", Enabled = false }));
        }
        else
        {
            for (var i = 0; i < visible.Count; i++)
            {
                var peer = visible[i];
                if (i > 0) root.Children.Add(Separator());

                root.Children.Add(New(new MenuItem
                {
                    Label = $"{peer.Name} — {Describe(peer)}",
                    Enabled = false,
                }));

                if (peer.State == PeerState.Pending)
                {
                    // The fingerprint is shown so it can be checked against
                    // the other device before trusting, which is the only
                    // verification two-sided TOFU offers.
                    root.Children.Add(New(new MenuItem
                    {
                        Label = $"    Trust this device ({peer.DidHex[..8]})",
                        Activate = () => actions.Trust(peer.DidHex),
                    }));
                    root.Children.Add(New(new MenuItem
                    {
                        Label = "    Hide this device",
                        Activate = () => actions.Hide(peer.DidHex, peer.Name),
                    }));
                }
                else
                {
                    var muted = state.IsMuted(peer.DidHex);
                    root.Children.Add(New(new MenuItem
                    {
                        Label = muted ? "    Resume sending to this device"
                                      : "    Pause sending to this device",
                        Checked = muted,
                        Activate = () => actions.SetMuted(peer.DidHex, !muted),
                    }));
                }
            }
        }

        if (state.Hidden.Count > 0)
        {
            root.Children.Add(Separator());
            root.Children.Add(New(new MenuItem
            {
                Label = $"Hidden devices ({state.Hidden.Count})",
                Enabled = false,
            }));
            foreach (var hidden in state.Hidden)
            {
                root.Children.Add(New(new MenuItem
                {
                    Label = $"    Show {hidden.Name}",
                    Activate = () => actions.Unhide(hidden.DidHex),
                }));
            }
        }

        root.Children.Add(Separator());
        root.Children.Add(New(new MenuItem { Label = "Quit ClipSync", Activate = actions.Quit }));

        Root = root;
    }

    private static string Describe(Peer peer) => peer.State switch
    {
        PeerState.Online => peer.Version is { } v ? $"Online, {v}" : "Online",
        PeerState.Pending => "Waiting to be trusted",
        PeerState.Looking => "Looking…",
        _ => "Offline",
    };

    private MenuItem Separator() => New(new MenuItem { IsSeparator = true, Enabled = false });

    private MenuItem New(MenuItem template)
    {
        var item = new MenuItem
        {
            Id = _nextId++,
            Label = template.Label,
            Enabled = template.Enabled,
            IsSeparator = template.IsSeparator,
            Checked = template.Checked,
            Activate = template.Activate,
        };
        _byId[item.Id] = item;
        return item;
    }

    /// Every item, root first — used to answer GetGroupProperties, which
    /// asks about ids rather than walking the tree.
    public IEnumerable<MenuItem> All()
    {
        IEnumerable<MenuItem> Walk(MenuItem item)
            => new[] { item }.Concat(item.Children.SelectMany(Walk));
        return Walk(Root);
    }
}
