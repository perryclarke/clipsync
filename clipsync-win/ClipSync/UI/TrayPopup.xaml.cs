using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
            if (e.WindowActivationState == WindowActivationState.Deactivated)
                Hide();
        };
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
        PeerList.Children.Clear();
        EmptyText.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var peer in peers)
        {
            switch (peer.State)
            {
                case PeerState.Online:
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
                    {
                        Width = 8, Height = 8, Fill = new SolidColorBrush(Colors.LimeGreen),
                        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
                    });
                    row.Children.Add(new TextBlock { Text = peer.Name, FontSize = 14 });
                    row.Children.Add(new TextBlock
                    {
                        Text = "Online",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Colors.Gray),
                        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
                    });
                    PeerList.Children.Add(row);
                    break;
                }
                case PeerState.Pending:
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
                    {
                        Width = 8, Height = 8, Fill = new SolidColorBrush(Colors.Orange),
                        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
                    });
                    row.Children.Add(new TextBlock
                    {
                        Text = peer.Name, FontSize = 14,
                        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
                    });
                    var btn = new Button { Content = "Trust", Padding = new Thickness(12, 2, 12, 2) };
                    var did = peer.DidHex;
                    var name = peer.Name;
                    btn.Click += (_, _) =>
                    {
                        App.Current.TrustStore.Add(did, name);
                        App.Current.Discovery.ConnectToPeer(did);
                    };
                    row.Children.Add(btn);
                    PeerList.Children.Add(row);
                    break;
                }
                case PeerState.Offline:
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
                    {
                        Width = 8, Height = 8, Fill = new SolidColorBrush(Colors.Gray),
                        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
                    });
                    row.Children.Add(new TextBlock { Text = peer.Name, FontSize = 14 });
                    row.Children.Add(new TextBlock
                    {
                        Text = "Offline",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Colors.Gray),
                        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
                    });
                    PeerList.Children.Add(row);
                    break;
                }
            }
        }
    }

    private void ShowAtCursor()
    {
        GetCursorPos(out var pt);

        // Find the display that contains the cursor.
        var displayArea = DisplayArea.GetFromPoint(
            new Windows.Graphics.PointInt32(pt.X, pt.Y),
            DisplayAreaFallback.Primary);

        int w = 320, h = 300;
        int x = pt.X - w / 2;
        int y = pt.Y - h;

        // Clamp to the work area of the display the cursor is on.
        var work = displayArea.WorkArea;
        if (x < work.X) x = work.X;
        if (x + w > work.X + work.Width) x = work.X + work.Width - w;
        if (y < work.Y) y = work.Y;
        if (y + h > work.Y + work.Height) y = work.Y + work.Height - h;

        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
        Activate();
    }

    private void Hide()
    {
        _appWindow.Hide();
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

