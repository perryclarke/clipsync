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
    private Func<bool>? _startAtLogin;
    private string _deviceName = "";
    private string _fingerprint = "";

    // UI thread only.
    private Adw.ApplicationWindow? _window;
    private Gtk.Box? _groups;
    private Gtk.ScrolledWindow? _scroll;
    private Gtk.Entry? _excludeEntry;
    private bool _rebuilding;

    public void Bind(Func<TrayState> state, TrayActions actions,
                     Func<bool> startAtLogin,
                     string deviceName, string fingerprint)
    {
        _state = state;
        _actions = actions;
        _startAtLogin = startAtLogin;
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
        _scroll = scroll;

        var view = Adw.ToolbarView.New();
        view.AddTopBar(Adw.HeaderBar.New());
        view.SetContent(scroll);

        var window = Adw.ApplicationWindow.New(_ui.Application!);
        window.SetTitle("ClipSync Settings");
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

        // A rebuild replaces every widget, which would wipe whatever is
        // typed into the add-exclusion field and snap the scroll position
        // back to the top — and peer churn rebuilds at arbitrary moments —
        // so both are carried across.
        var pendingExclusion = _excludeEntry?.GetText() ?? "";
        var scrollOffset = _scroll?.GetVadjustment()?.GetValue() ?? 0;

        _rebuilding = true;
        try
        {
            while (_groups!.GetFirstChild() is { } child) _groups.Remove(child);
            _groups.Append(SyncingGroup(content));
            _groups.Append(DevicesGroup(content));
            if (content.Hidden.Count > 0) _groups.Append(HiddenGroup(content));
            _groups.Append(ExcludedGroup(content, pendingExclusion));
            _groups.Append(GeneralGroup());
            _groups.Append(DebugGroup());
        }
        finally { _rebuilding = false; }

        if (scrollOffset > 0)
        {
            // Deferred at below-redraw priority: the adjustment's range is
            // recalculated during layout, and a value set before that is
            // clamped back to 0.
            GLib.Functions.IdleAdd(200, () =>
            {
                _scroll?.GetVadjustment()?.SetValue(scrollOffset);
                return false;
            });
        }
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

    private Adw.PreferencesGroup ExcludedGroup(WindowContent content, string pendingText)
    {
        var group = Adw.PreferencesGroup.New();
        group.SetTitle(WindowModel.ExcludedTitle);
        group.SetDescription(WindowModel.ExcludedDescription);

        if (content.Excluded.Count == 0)
        {
            var empty = Adw.ActionRow.New();
            empty.SetTitle(WindowModel.NoExclusions);
            empty.SetSensitive(false);
            group.Add(empty);
        }

        foreach (var app in content.Excluded)
        {
            var row = Adw.ActionRow.New();
            row.SetUseMarkup(false);
            row.SetTitle(app.Title);

            var remove = Gtk.Button.NewFromIconName("user-trash-symbolic");
            remove.AddCssClass("flat");
            remove.SetValign(Gtk.Align.Center);
            remove.SetTooltipText(WindowModel.RemoveTooltip);
            var key = app.Key;
            remove.OnClicked += (_, _) => _actions!.RemoveExclusion(key);
            row.AddSuffix(remove);

            group.Add(row);
        }

        // A plain entry rather than Adw.EntryRow, which Gir.Core 0.8.1
        // does not bind. There is no app picker on purpose: exclusions are
        // keyed on WM_CLASS (see the design doc), and `xprop WM_CLASS` is
        // the discovery tool.
        var entry = Gtk.Entry.New();
        entry.SetPlaceholderText(WindowModel.ExcludePlaceholder);
        entry.SetText(pendingText);
        entry.SetHexpand(true);
        _excludeEntry = entry;

        var add = Gtk.Button.NewWithLabel(WindowModel.AddLabel);
        void Submit()
        {
            var wmClass = entry.GetText().Trim();
            if (wmClass.Length == 0) return;
            _excludeEntry = null;              // adding clears the field
            _actions!.AddExclusion(wmClass);
        }
        add.OnClicked += (_, _) => Submit();
        entry.OnActivate += (_, _) => Submit();

        var addBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        addBox.AddCssClass("linked");
        addBox.SetMarginTop(6);
        addBox.Append(entry);
        addBox.Append(add);
        group.Add(addBox);

        return group;
    }

    private Adw.PreferencesGroup GeneralGroup()
    {
        var group = Adw.PreferencesGroup.New();
        group.SetTitle(WindowModel.GeneralTitle);

        var login = Adw.SwitchRow.New();
        login.SetTitle(WindowModel.LoginTitle);
        login.SetActive(_startAtLogin!());
        login.OnNotify += (_, args) =>
        {
            if (_rebuilding || args.Pspec.GetName() != "active") return;
            _actions!.SetStartAtLogin(login.GetActive());
        };
        group.Add(login);

        var reset = Adw.ActionRow.New();
        reset.SetTitle(WindowModel.StartOverLabel);
        var button = Gtk.Button.NewWithLabel(WindowModel.StartOverConfirm);
        button.AddCssClass("destructive-action");
        button.SetValign(Gtk.Align.Center);
        button.OnClicked += (_, _) => ConfirmStartOver();
        reset.AddSuffix(button);
        reset.SetActivatableWidget(button);
        group.Add(reset);

        return group;
    }

    /// The Debug group. macOS and Windows hide theirs behind Option/Alt at
    /// summon time; a dbusmenu activation carries no modifier state, so on
    /// Linux it is simply always here — which also suits the platform where
    /// the app is most often run from a terminal.
    private Adw.PreferencesGroup DebugGroup()
    {
        var group = Adw.PreferencesGroup.New();
        group.SetTitle(WindowModel.DebugTitle);

        var logging = Adw.SwitchRow.New();
        logging.SetUseMarkup(false);
        logging.SetTitle(WindowModel.DebugLoggingTitle);
        logging.SetSubtitle(WindowModel.DebugLoggingSubtitle);
        logging.SetActive(Identity.LoggingEnabled);
        logging.OnNotify += (_, args) =>
        {
            if (_rebuilding || args.Pspec.GetName() != "active") return;
            _actions!.SetDebugLogging(logging.GetActive());
        };
        group.Add(logging);

        var log = Adw.ActionRow.New();
        log.SetUseMarkup(false);
        log.SetTitle(WindowModel.DebugLogTitle);
        log.SetSubtitle(WindowModel.DebugLogSubtitle);
        var copy = Gtk.Button.NewWithLabel(WindowModel.DebugCopyLabel);
        copy.SetValign(Gtk.Align.Center);
        copy.SetTooltipText(WindowModel.DebugLogCommand);
        copy.OnClicked += (_, _) => _actions!.CopyLogCommand();
        log.AddSuffix(copy);
        log.SetActivatableWidget(copy);
        group.Add(log);

        return group;
    }

    private void ConfirmStartOver()
    {
        var dialog = Adw.AlertDialog.New(WindowModel.StartOverHeading,
                                         WindowModel.StartOverBody);
        dialog.AddResponse("cancel", WindowModel.CancelLabel);
        dialog.AddResponse("reset", WindowModel.StartOverConfirm);
        dialog.SetResponseAppearance("reset", Adw.ResponseAppearance.Destructive);
        dialog.SetDefaultResponse("cancel");
        dialog.SetCloseResponse("cancel");
        dialog.OnResponse += (_, args) =>
        {
            if (args.Response == "reset") _actions!.StartOver();
        };
        dialog.Present(_window);
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
