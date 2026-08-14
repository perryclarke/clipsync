using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClipSync.Net;
using ClipSync.Security;
using WinRT.Interop;

namespace ClipSync.UI;

public sealed partial class TrayPopup : Window
{
    private static TrayPopup? _instance;
    private AppWindow _appWindow;

    public TrayPopup()
    {
        InitializeComponent();
        DidText.Text = "me: " + Identity.Current.DidHex[..8];

        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);

        // Remove title bar, make it a tool window (no taskbar entry).
        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        var presenter = OverlappedPresenter.CreateForContextMenu();

        // Topmost, or this opens underneath the thing it was opened from.
        // The notification area's own overflow flyout -- the grid of hidden
        // icons that holds our tray icon -- is a topmost window, and being
        // the foreground window does not put us above one of those. Windows'
        // own tray flyouts are topmost for the same reason.
        presenter.IsAlwaysOnTop = true;

        _appWindow.SetPresenter(presenter);

        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) return;

            // Losing focus to another app dismisses this popup, as a tray
            // flyout should. Losing it to one of our own windows does not:
            // that is the settings window opening beside us, and the whole
            // point of placing it beside us is that both stay on screen.
            if (ForegroundIsOurs()) return;

            // The check above is not enough on its own. Activating the
            // settings window makes this one deactivate, but Windows sets the
            // new foreground asynchronously, so the deactivation can arrive
            // while GetForegroundWindow still names somebody else -- and then
            // the popup vanishes exactly when it was meant to stay. A short
            // grace period after the click covers that gap without leaving a
            // latch that could strand the popup on screen: it expires on its
            // own whether or not the race happened.
            if (DateTime.UtcNow - _settingsOpenedAt < SettingsFocusGrace) return;

            Hide();
        };
    }

    /// True when the window now in front belongs to this process.
    private static bool ForegroundIsOurs()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            // Unknowable means "not ours", which keeps the old
            // dismiss-on-deactivate behaviour rather than a stuck popup.
            return false;
        }
    }

    public static void Toggle()
    {
        if (_instance is not null && _instance._appWindow.IsVisible)
        {
            _instance.Hide();
            return;
        }
        _instance ??= new TrayPopup();
        _instance.Refresh(App.Current.Peers.GetAll());
        _instance.ShowAtCursor();
    }

    public static void RefreshIfVisible(IReadOnlyList<Peer> peers)
    {
        if (_instance is not null && _instance._appWindow.IsVisible)
            _instance.Refresh(peers);
    }

    private void Refresh(IReadOnlyList<Peer> peers)
    {
        _peerCount = peers.Count;
        PeerList.Children.Clear();
        EmptyText.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Identity.Log($"Refresh: {peers.Count} peers");

        var first = true;
        foreach (var peer in peers)
        {
            Identity.Log($"  peer: {peer.Name} state={peer.State}");
            PeerList.Children.Add(BuildPeerRow(peer, first));
            first = false;
        }

        RefreshPauseButton();
    }

    /// The global control says what pressing it will do; the title says what
    /// is currently true. Both, because a button alone reading "Resume
    /// syncing" is a weak way to learn that you are not syncing.
    private void RefreshPauseButton()
    {
        var paused = App.Current.Pause.GlobalPaused;
        PauseLabel.Text = paused ? "Resume syncing" : "Pause syncing";
        PauseGlyph.Glyph = paused ? PlayGlyph : PauseGlyphText;
        AutomationProperties.SetName(PauseButton, PauseLabel.Text);
        TitleText.Text = paused ? "ClipSync — Paused" : "ClipSync";
    }

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        var pause = App.Current.Pause;
        pause.GlobalPaused = !pause.GlobalPaused;
        App.Current.Tray.RefreshTooltip();
        // Rebuilds the rows too: a global pause does not change any peer's
        // mute, but it does change what the popup as a whole is saying.
        Refresh(App.Current.Peers.GetAll());
    }

    /// One peer line: a status dot, the peer's name, and then either the
    /// status in words or the Trust button a pending peer needs.
    ///
    /// The three states were three near-identical copies of this; the only
    /// thing that ever varied is the tuple below.
    private UIElement BuildPeerRow(Peer peer, bool first)
    {
        // A muted peer reads as paused whatever its connection is doing: the
        // reason nothing reaches it is the mute, not the network, and saying
        // "Online" there would be actively misleading.
        var muted = peer.State != PeerState.Pending && App.Current.Pause.IsMuted(peer.DidHex);

        var (brushKey, fallback, status) = peer.State switch
        {
            _ when muted      => ("SystemFillColorCautionBrush", Colors.Orange, "Paused"),
            PeerState.Online  => ("SystemFillColorSuccessBrush", Colors.LimeGreen, "Online"),
            PeerState.Pending => ("SystemFillColorCautionBrush", Colors.Orange, "Waiting to be trusted"),
            _                 => ("TextFillColorDisabledBrush", Colors.Gray, "Offline"),
        };

        // Four columns: dot, name, state, action. The star on the state
        // column is what pushes the button to the right edge, which is where
        // a Windows 11 list row puts its control.
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 10, 12, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The dot is decorative: the state is always written out beside it,
        // so colour never carries meaning on its own -- which is what both a
        // contrast theme and a colour-blind user need. Marking it Raw keeps a
        // screen reader from stopping on a shape with nothing to say.
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            Fill = Themed(brushKey, fallback),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAccessibilityView(dot, AccessibilityView.Raw);
        Grid.SetColumn(dot, 0);
        row.Children.Add(dot);

        var nameText = new TextBlock
        {
            Text = peer.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(nameText, 1);
        row.Children.Add(nameText);

        if (peer.State == PeerState.Pending)
        {
            var did = peer.DidHex;
            var name = peer.Name;
            var btn = new Button
            {
                Content = "Trust",
                Padding = new Thickness(12, 2, 12, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            // "Trust" on its own is ambiguous once there are two pending
            // peers, and trusting the wrong device is not a small mistake.
            AutomationProperties.SetName(btn, $"Trust {name}");
            btn.Click += (_, _) =>
            {
                Identity.Log($"Trust clicked: {name} did={did}");
                App.Current.TrustStore.Add(did, name);
                App.Current.Discovery.ConnectToPeer(did);
            };
            Grid.SetColumn(btn, 3);
            row.Children.Add(btn);
        }
        else
        {
            var stateText = new TextBlock
            {
                Text = status, FontSize = 12,
                Foreground = Themed("TextFillColorSecondaryBrush", Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(stateText, 2);
            row.Children.Add(stateText);

            var mute = BuildMuteButton(peer, muted);
            Grid.SetColumn(mute, 3);
            row.Children.Add(mute);
        }

        // Dividers are drawn by the row below, never by the one above, so no
        // two borders meet and nothing doubles to two pixels. Same rule the
        // settings window's list follows.
        return new Border
        {
            Child = row,
            BorderBrush = Themed("CardStrokeColorDefaultBrush", Colors.Gray),
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
        };
    }

    /// Per-peer send toggle. Offered on known peers whether they are online
    /// or not: the mute persists, so muting a machine that happens to be off
    /// right now is a reasonable thing to want. A pending peer has no such
    /// button -- it is not syncing yet, and its row already has Trust.
    private FrameworkElement BuildMuteButton(Peer peer, bool muted)
    {
        var did = peer.DidHex;
        var name = peer.Name;

        // Verb, not state: the button says what pressing it will do.
        var label = muted ? $"Resume syncing to {name}" : $"Pause syncing to {name}";
        var btn = new Button
        {
            Content = new FontIcon { Glyph = muted ? PlayGlyph : PauseGlyphText, FontSize = 12 },
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(btn, label);
        ToolTipService.SetToolTip(btn, label);

        btn.Click += (_, _) =>
        {
            App.Current.Pause.SetMuted(did, !muted);
            Refresh(App.Current.Peers.GetAll());
        };
        return btn;
    }

    private const string PauseGlyphText = "\uE769";  // Pause
    private const string PlayGlyph      = "\uE768";  // Play

    private int _peerCount;

    /// A theme brush by key, with a literal fallback.
    ///
    /// These rows are built in code, where `{ThemeResource}` isn't available;
    /// a hard cast would throw on any theme that omits the key, and this runs
    /// mid-refresh where a throw would leave the popup half-populated.
    private static Brush Themed(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    private void ShowAtCursor()
    {
        GetCursorPos(out var pt);

        // Find the display that contains the cursor.
        var displayArea = DisplayArea.GetFromPoint(
            new Windows.Graphics.PointInt32(pt.X, pt.Y),
            DisplayAreaFallback.Primary);

        // Scale factor: WinUI uses physical pixels for AppWindow.
        var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        var scale = dpi / 96.0;

        // Ask the content how big it is rather than adding the parts up.
        var (w, h) = MeasuredSize(rows: Math.Max(_peerCount, 1), scale: scale);

        // Anchored to the notification area rather than to the cursor: this
        // opens from a tray icon, which lives in the bottom corner, and that
        // is where Windows puts its own tray flyouts. Following the cursor
        // instead put it wherever the pointer happened to be and left a
        // 200px gap above the taskbar that belonged to nothing.
        var work = displayArea.WorkArea;
        int margin = (int)(12 * scale);
        int x = work.X + work.Width - w - margin;
        int y = work.Y + work.Height - h - margin;

        // Clamp to the work area for the case of a display narrower than the
        // popup, where the margin cannot be honoured.
        if (x < work.X) x = work.X;
        if (y < work.Y) y = work.Y;

        var bounds = new Windows.Graphics.RectInt32(x, y, w, h);
        _appWindow.MoveAndResize(bounds);
        Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(this));

        // Stored in AppWindow's own coordinates, which is what the settings
        // window does its arithmetic in. Storing a rectangle read back from
        // DWM instead was a bug: DWM answers in raw pixels, AppWindow does
        // not always, and mixing the two threw the settings window onto
        // another display entirely.
        _bounds = bounds;
    }

    /// Narrowest and widest the popup is allowed to be, in DIPs. The floor
    /// keeps it from looking mean with one short peer name; the ceiling keeps
    /// a machine called something absurd from producing a popup half the
    /// width of the screen.
    private const double MinWidthDip = 300;
    private const double MaxWidthDip = 440;

    /// How big the popup's content wants to be, in physical pixels.
    ///
    /// Measured from the live tree, so anything added to the XAML is counted
    /// without a second edit here. The hand-summed height this replaces had
    /// to be updated every time a control was added and twice was not, which
    /// clips the bottom item and reads as a layout bug rather than a stale
    /// constant. The width was worse: it was never computed at all, just a
    /// round number that the content had quietly outgrown.
    ///
    /// Falls back to that estimate if the content reports nothing, which it
    /// does before its first layout pass: a roughly-right window beats one of
    /// height zero.
    private (int Width, int Height) MeasuredSize(int rows, double scale)
    {
        try
        {
            if (Content is FrameworkElement root)
            {
                root.Measure(new Windows.Foundation.Size(MaxWidthDip, double.PositiveInfinity));
                var wanted = root.DesiredSize;
                if (wanted.Height > 0 && wanted.Width > 0)
                {
                    var w = Math.Clamp(wanted.Width, MinWidthDip, MaxWidthDip);
                    return ((int)Math.Ceiling(w * scale),
                            (int)Math.Ceiling(wanted.Height * scale));
                }
            }
        }
        catch (Exception ex)
        {
            Identity.Log($"TrayPopup: measure failed: {ex.GetType().Name}");
        }

        // header + peers card + 3 buttons + padding, as it was before.
        return ((int)(MinWidthDip * scale),
                (int)((24 + 18 + 1 + (rows * 40) + 1 + 36 + 36 + 36 + 8 + 60) * scale));
    }

    /// Where this popup was last drawn, in physical pixels, so the settings
    /// window can sit beside it. Null until it has been shown once.
    private static Windows.Graphics.RectInt32? _bounds;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private void Hide()
    {
        _appWindow.Hide();
    }

    /// How long after opening settings a deactivation is assumed to be the
    /// settings window taking focus rather than the user clicking away.
    private static readonly TimeSpan SettingsFocusGrace = TimeSpan.FromMilliseconds(1500);

    private DateTime _settingsOpenedAt = DateTime.MinValue;

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        // Deliberately not hidden: the settings window places itself beside
        // this popup, and the deactivation handler above knows to leave us
        // alone when the window taking focus is one of ours.
        _settingsOpenedAt = DateTime.UtcNow;
        SettingsWindow.ShowSingleton(_bounds);
    }

    private void OnQuit(object sender, RoutedEventArgs e)
    {
        Application.Current.Exit();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
}

