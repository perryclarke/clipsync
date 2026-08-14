using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using H.NotifyIcon;
using ClipSync.Security;

namespace ClipSync.UI;

public sealed class TrayIcon
{
    private TaskbarIcon? _icon;
    private Icon? _normalIcon;
    private Icon? _pausedIcon;

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
        _normalIcon = LoadIcon();
        _icon.Icon = _normalIcon;
        _icon.ForceCreate();

        App.Current.Peers.OnChange = peers =>
        {
            _icon.DispatcherQueue?.TryEnqueue(() => TrayPopup.RefreshIfVisible(peers));
        };
    }

    /// The tray is the only place a global pause shows without opening the
    /// popup, so both the icon and its tooltip follow the state.
    public void RefreshState()
    {
        if (_icon is null || _normalIcon is null) return;

        var paused = App.Current.Pause.GlobalPaused;
        _icon.ToolTipText = paused ? "ClipSync — paused" : "ClipSync";

        // Built once and kept: Icon.FromHandle does not own its HICON, so
        // recomposing on every toggle would leak one handle per press.
        if (paused) _pausedIcon ??= BuildPausedIcon(_normalIcon);
        var wanted = paused ? _pausedIcon : _normalIcon;
        if (wanted is not null) _icon.Icon = wanted;
    }

    private static Icon LoadIcon()
    {
        const string resourceName = "ClipSync.Assets.AppIcon.ico";
        var asm = typeof(TrayIcon).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded icon '{resourceName}' not found.");
        return new Icon(stream);
    }

    /// The app icon with a pause badge in the corner, the way OneDrive marks
    /// itself paused.
    ///
    /// Composed here rather than shipped as a second .ico so it cannot drift
    /// out of step with the real icon, and so the badge scales with whatever
    /// size the tray asks for. A solid dark disc with a light ring reads on
    /// both a light and a dark taskbar, which a flat glyph would not.
    ///
    /// Returns the unbadged icon if anything goes wrong: a tray icon missing
    /// its overlay is a cosmetic loss, no icon at all is a missing app.
    private static Icon? BuildPausedIcon(Icon source)
    {
        const int Size = 32;
        try
        {
            using var scaled = new Icon(source, Size, Size);
            using var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (var baseBmp = scaled.ToBitmap())
                    g.DrawImage(baseBmp, 0, 0, Size, Size);

                // Bottom-right badge, sized as a fraction of the icon so this
                // holds at whatever Size becomes.
                float d = Size * 0.60f;
                float x = Size - d, y = Size - d;

                using var disc = new SolidBrush(Color.FromArgb(230, 24, 24, 24));
                using var ring = new Pen(Color.FromArgb(235, 255, 255, 255), Size * 0.055f);
                g.FillEllipse(disc, x, y, d, d);
                g.DrawEllipse(ring, x, y, d, d);

                // Two bars, centred in the disc.
                float barW = d * 0.15f, barH = d * 0.42f, gap = d * 0.14f;
                float cx = x + d / 2f, cy = y + d / 2f;
                using var bar = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
                g.FillRectangle(bar, cx - gap / 2f - barW, cy - barH / 2f, barW, barH);
                g.FillRectangle(bar, cx + gap / 2f, cy - barH / 2f, barW, barH);
            }

            // GetHicon hands back a handle this Icon will not free, so take
            // ownership of it explicitly and keep it for the process.
            var handle = bmp.GetHicon();
            using var temp = Icon.FromHandle(handle);
            var owned = (Icon)temp.Clone();
            DestroyIcon(handle);
            return owned;
        }
        catch (Exception ex)
        {
            Identity.Log($"TrayIcon: could not build the paused icon: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _action;
    public RelayCommand(Action action) => _action = action;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
}
