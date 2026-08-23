using System;
using ClipSync.Security;

namespace ClipSync.Ui;

/// The app window: the primary Linux UI, opened by clicking the tray icon.
///
/// Renders what WindowModel says and nothing else — the shape and wording
/// live there, where the tests can reach them. Open and Refresh are safe
/// from any thread; everything GTK stays on the UiThread.
///
/// The window hides on close rather than quitting: it is a view over the
/// daemon, not the app.
internal sealed class MainWindow
{
    private readonly UiThread _ui = new();

    private Func<TrayState>? _state;
    private TrayActions? _actions;
    private string _deviceName = "";
    private string _fingerprint = "";

    // UI thread only.
    private Adw.ApplicationWindow? _window;
    private Gtk.Box? _groups;
    private bool _rebuilding;

    public void Bind(Func<TrayState> state, TrayActions actions,
                     string deviceName, string fingerprint)
    {
        _state = state;
        _actions = actions;
        _deviceName = deviceName;
        _fingerprint = fingerprint;
    }

    public void Open() => _ui.Post(() =>
    {
        EnsureWindow();
        Rebuild();
        _window!.Present();
        // Positive confirmation, because "no Ui error" is ambiguous
        // between "worked" and "never ran".
        Identity.Log("Ui: window presented");
    });

    /// Repaint if the window is showing; a hidden window is rebuilt on the
    /// next Open instead, so peer churn while it is closed costs nothing.
    public void Refresh() => _ui.Post(() =>
    {
        if (_window is null || !_window.GetVisible()) return;
        Rebuild();
    });

    private void EnsureWindow()
    {
        if (_window is not null) return;

        _groups = Gtk.Box.New(Gtk.Orientation.Vertical, 24);
        _groups.SetMarginTop(24);
        _groups.SetMarginBottom(24);
        _groups.SetMarginStart(16);
        _groups.SetMarginEnd(16);

        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(560);
        clamp.SetChild(_groups);

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetChild(clamp);

        var view = Adw.ToolbarView.New();
        view.AddTopBar(Adw.HeaderBar.New());
        view.SetContent(scroll);

        var window = Adw.ApplicationWindow.New(_ui.Application!);
        window.SetTitle("ClipSync");
        window.SetDefaultSize(420, 540);
        window.SetHideOnClose(true);
        window.SetContent(view);
        _window = window;
    }

    /// Regenerate every group from the model. Coarse on purpose: the lists
    /// are a handful of rows, and rebuilding beats reconciling widget
    /// state against daemon state.
    private void Rebuild()
    {
        var content = WindowModel.Build(_state!(), _deviceName, _fingerprint);

        _rebuilding = true;
        try
        {
            while (_groups!.GetFirstChild() is { } child) _groups.Remove(child);
            _groups.Append(SyncingGroup(content));
            _groups.Append(DevicesGroup(content));
            if (content.Hidden.Count > 0) _groups.Append(HiddenGroup(content));
        }
        finally { _rebuilding = false; }
    }

    private Adw.PreferencesGroup SyncingGroup(WindowContent content)
    {
        var group = Adw.PreferencesGroup.New();

        var pauseRow = Adw.SwitchRow.New();
        pauseRow.SetUseMarkup(false);
        pauseRow.SetTitle(WindowModel.PauseTitle);
        pauseRow.SetSubtitle(WindowModel.PauseSubtitle);
        pauseRow.SetActive(content.Paused);
        pauseRow.OnNotify += (_, args) =>
        {
            if (_rebuilding || args.Pspec.GetName() != "active") return;
            _actions!.SetPaused(pauseRow.GetActive());
        };
        group.Add(pauseRow);

        var device = Adw.ActionRow.New();
        device.SetUseMarkup(false);
        device.SetTitle("This device");
        device.SetSubtitle($"{content.DeviceName} — {content.Fingerprint}");
        group.Add(device);

        return group;
    }

    private Adw.PreferencesGroup DevicesGroup(WindowContent content)
    {
        var group = Adw.PreferencesGroup.New();
        group.SetTitle(WindowModel.DevicesTitle);

        if (content.Devices.Count == 0)
        {
            var empty = Adw.ActionRow.New();
            empty.SetTitle(WindowModel.NoDevices);
            empty.SetSensitive(false);
            group.Add(empty);
            return group;
        }

        foreach (var device in content.Devices)
        {
            var row = Adw.ActionRow.New();
            // Peer names arrive off the network; markup titles would let
            // one style itself.
            row.SetUseMarkup(false);
            row.SetTitle(device.Title);
            row.SetSubtitle(device.Subtitle);

            if (device.OffersTrust)
            {
                var trust = Gtk.Button.NewWithLabel(WindowModel.TrustLabel);
                trust.AddCssClass("suggested-action");
                trust.SetValign(Gtk.Align.Center);
                var did = device.Did;
                trust.OnClicked += (_, _) => _actions!.Trust(did);
                row.AddSuffix(trust);
            }

            if (device.OffersSendSwitch)
            {
                var send = Gtk.Switch.New();
                send.SetActive(device.Sending);
                send.SetValign(Gtk.Align.Center);
                send.SetTooltipText(WindowModel.SendTooltip);
                var did = device.Did;
                send.OnNotify += (_, args) =>
                {
                    if (_rebuilding || args.Pspec.GetName() != "active") return;
                    _actions!.SetMuted(did, !send.GetActive());
                };
                row.AddSuffix(send);
                row.SetActivatableWidget(send);
            }

            if (device.OffersHide)
            {
                var hide = Gtk.Button.NewFromIconName("view-conceal-symbolic");
                hide.AddCssClass("flat");
                hide.SetValign(Gtk.Align.Center);
                hide.SetTooltipText(WindowModel.HideTooltip);
                var did = device.Did;
                var name = device.Title;
                hide.OnClicked += (_, _) => _actions!.Hide(did, name);
                row.AddSuffix(hide);
            }

            group.Add(row);
        }

        return group;
    }

    private Adw.PreferencesGroup HiddenGroup(WindowContent content)
    {
        var group = Adw.PreferencesGroup.New();
        group.SetTitle(WindowModel.HiddenTitle);

        foreach (var hidden in content.Hidden)
        {
            var row = Adw.ActionRow.New();
            row.SetUseMarkup(false);
            row.SetTitle(hidden.Title);

            var show = Gtk.Button.NewWithLabel(WindowModel.ShowLabel);
            show.SetValign(Gtk.Align.Center);
            var did = hidden.Did;
            show.OnClicked += (_, _) => _actions!.Unhide(did);
            row.AddSuffix(show);

            group.Add(row);
        }

        return group;
    }
}
