using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using H.NotifyIcon;
using ClipSync.Security;

namespace ClipSync.UI;

public sealed class TrayIcon
{
    private TaskbarIcon? _icon;

    public void Show()
    {
        var cmd = new RelayCommand(() => TrayPopup.Toggle());
        _icon = new TaskbarIcon
        {
            ToolTipText = "ClipSync",
            LeftClickCommand = cmd,
            RightClickCommand = cmd,
            NoLeftClickDelay = true,
        };
        _icon.Icon = LoadIcon();
        _icon.ForceCreate();

        App.Current.Peers.OnChange = peers =>
        {
            _icon.DispatcherQueue?.TryEnqueue(() => TrayPopup.RefreshIfVisible(peers));
        };
    }

    private static Icon LoadIcon()
    {
        const string resourceName = "ClipSync.Assets.AppIcon.ico";
        var asm = typeof(TrayIcon).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded icon '{resourceName}' not found.");
        return new Icon(stream);
    }
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _action;
    public RelayCommand(Action action) => _action = action;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
}
