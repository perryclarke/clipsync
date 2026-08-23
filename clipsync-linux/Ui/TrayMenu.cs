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

/// What the UIs show.
internal sealed record TrayState(
    IReadOnlyList<Peer> Peers,
    bool Paused,
    Func<string, bool> IsMuted,
    IReadOnlyList<HiddenPeer> Hidden,
    IReadOnlyList<Settings.AppIdentity> Excluded);

/// What the UIs can do. The menu uses the first few; the rest exist for
/// the window's settings groups.
internal sealed record TrayActions(
    Action<bool> SetPaused,
    Action<string> Trust,
    Action<string, bool> SetMuted,
    Action<string, string> Hide,
    Action<string> Unhide,
    Action OpenWindow,
    Action Quit,
    Action<string> AddExclusion,
    Action<string> RemoveExclusion,
    Action<bool> SetStartAtLogin,
    Action StartOver);

/// Builds the tray menu: the right-click fallback, not the primary UI.
///
/// The app window (see WindowModel) is where devices are trusted, paused
/// and hidden. This menu exists for two reasons: hosts that never route
/// Activate still need a way to open the window, and pause/quit should
/// stay one click away. It is deliberately three items — everything
/// arriving over dbusmenu renders as a flat list of one-shot actions that
/// closes on every click (GNOME's AppIndicator extension; the richer
/// shapes were tried and don't work, see the design doc), so anything
/// needing a sequence of interactions belongs in the window.
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
            Label = "Open ClipSync",
            Activate = actions.OpenWindow,
        }));

        root.Children.Add(New(new MenuItem
        {
            Label = state.Paused ? "Resume Syncing" : "Pause Syncing",
            Checked = state.Paused,
            Activate = () => actions.SetPaused(!state.Paused),
        }));

        root.Children.Add(New(new MenuItem { IsSeparator = true, Enabled = false }));
        root.Children.Add(New(new MenuItem { Label = "Quit ClipSync", Activate = actions.Quit }));

        Root = root;
    }

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
