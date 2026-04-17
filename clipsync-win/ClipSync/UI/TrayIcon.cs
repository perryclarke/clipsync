using System;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

namespace ClipSync.UI;

/// Minimal tray icon with a context menu. The full WinUI 3 flyout/menu
/// UX will be fleshed out once the core sync loop is proven
/// cross-platform.
public sealed class TrayIcon
{
    private TaskbarIcon? _icon;

    public void Show()
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "ClipSync",
            ContextFlyout = BuildMenu(),
        };
        _icon.ForceCreate();
    }

    private static MenuFlyout BuildMenu()
    {
        var mf = new MenuFlyout();
        var pair = new MenuFlyoutItem { Text = "Pair new device…" };
        pair.Click += (_, _) => PairWindow.ShowInstance();
        mf.Items.Add(pair);
        mf.Items.Add(new MenuFlyoutSeparator());
        var quit = new MenuFlyoutItem { Text = "Quit" };
        quit.Click += (_, _) => Microsoft.UI.Xaml.Application.Current.Exit();
        mf.Items.Add(quit);
        return mf;
    }
}
