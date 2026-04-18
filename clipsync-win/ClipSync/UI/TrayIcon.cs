using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Input;
using H.NotifyIcon;
using ClipSync.Security;

namespace ClipSync.UI;

public sealed class TrayIcon
{
    private TaskbarIcon? _icon;

    public void Show()
    {
        var icoPath = GetOrCreateIcon();
        var cmd = new RelayCommand(() => TrayPopup.Toggle());
        _icon = new TaskbarIcon
        {
            ToolTipText = "ClipSync",
            LeftClickCommand = cmd,
            RightClickCommand = cmd,
            NoLeftClickDelay = true,
        };
        _icon.Icon = new Icon(icoPath);
        _icon.ForceCreate();

        App.Current.Peers.OnChange = peers =>
        {
            _icon.DispatcherQueue?.TryEnqueue(() => TrayPopup.RefreshIfVisible(peers));
        };
    }

    private static string GetOrCreateIcon()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipSync");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tray.ico");
        if (File.Exists(path)) return path;

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(Color.FromArgb(0, 120, 212));
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 12f, FontStyle.Bold);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("CS", font, Brushes.White, new RectangleF(0, 0, 32, 32), sf);
        }
        using var ico = Icon.FromHandle(bmp.GetHicon());
        using var fs = File.Create(path);
        ico.Save(fs);
        return path;
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
