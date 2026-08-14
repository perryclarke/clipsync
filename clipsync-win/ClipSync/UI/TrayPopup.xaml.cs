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

        foreach (var peer in peers)
        {
            Identity.Log($"  peer: {peer.Name} state={peer.State}");
            PeerList.Children.Add(BuildPeerRow(peer));
        }
    }

    /// One peer line: a status dot, the peer's name, and then either the
    /// status in words or the Trust button a pending peer needs.
    ///
    /// The three states were three near-identical copies of this; the only
    /// thing that ever varied is the tuple below.
    private UIElement BuildPeerRow(Peer peer)
    {
        var (brushKey, fallback, status) = peer.State switch
        {
            PeerState.Online  => ("SystemFillColorSuccessBrush", Colors.LimeGreen, "Online"),
            PeerState.Pending => ("SystemFillColorCautionBrush", Colors.Orange, "Waiting to be trusted"),
            _                 => ("TextFillColorDisabledBrush", Colors.Gray, "Offline"),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

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
        row.Children.Add(dot);

        row.Children.Add(new TextBlock
        {
            Text = peer.Name, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });

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
            row.Children.Add(btn);
        }
        else
        {
            row.Children.Add(new TextBlock
            {
                Text = status, FontSize = 12,
                Foreground = Themed("TextFillColorSecondaryBrush", Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return row;
    }

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

        // Compute height to fit content: header + DID + separator + peers + separator + button + padding.
        int rows = Math.Max(_peerCount, 1); // at least 1 for "Looking for peers..." text
        // header + DID + separator + peers + separator + 2 buttons + padding
        int contentHeight = (int)((24 + 18 + 1 + (rows * 28) + 1 + 36 + 36 + 8 + 60) * scale);
        int w = (int)(300 * scale), h = contentHeight;

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

