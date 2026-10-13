using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using H.NotifyIcon;
using ClipSync.Net;
using ClipSync.Security;

namespace ClipSync.UI;

public sealed class TrayIcon
{
    private TaskbarIcon? _icon;
    private Icon? _normalIcon;
    private Icon? _pausedIcon;
    private Icon? _attentionIcon;

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
            _icon.DispatcherQueue?.TryEnqueue(() =>
            {
                TrayPopup.RefreshIfVisible(peers);
                // A device going pending has to reach the tray icon, or a
                // peer waiting to be trusted is invisible until the user
                // happens to open the popup.
                RefreshState();
            });
        };
    }

    /// A device that has tried to connect is waiting to be trusted. The one
    /// state that needs the user to act, so it outranks the pause badge: a
    /// pause is something they chose and the popup spells out anyway, while a
    /// peer knocking is otherwise invisible until they go looking. A peer
    /// merely *seen* advertising does not count — on a shared network that
    /// would badge the tray for every stranger's ClipSync. Those still get a
    /// row and a Trust button. Hidden devices are a display preference, so
    /// they do not raise it either.
    private static bool NeedsAttention()
        => App.Current.Peers.GetAll().Any(p => p.State == PeerState.Pending
                                               && p.Knocked
                                               && !App.Current.Settings.IsHidden(p.DidHex));

    /// The tray is the only place a global pause — or a device waiting to be
    /// trusted — shows without opening the popup, so both the icon and its
    /// tooltip follow the state.
    public void RefreshState()
    {
        if (_icon is null || _normalIcon is null) return;

        var attention = NeedsAttention();
        var paused = App.Current.Pause.GlobalPaused;
        _icon.ToolTipText = attention ? "ClipSync — a device is asking to be trusted"
            : paused ? "ClipSync — paused"
            : "ClipSync";

        // Built once and kept: Icon.FromHandle does not own its HICON, so
        // recomposing on every change would leak one handle per press.
        if (attention) _attentionIcon ??= BuildBadgedIcon(_normalIcon, Badge.Attention);
        else if (paused) _pausedIcon ??= BuildBadgedIcon(_normalIcon, Badge.Pause);
        var wanted = attention ? _attentionIcon : paused ? _pausedIcon : _normalIcon;
        if (wanted is not null) _icon.Icon = wanted;
    }

    private enum Badge { Pause, Attention }

    private static Icon LoadIcon()
    {
        const string resourceName = "ClipSync.Assets.AppIcon.ico";
        var asm = typeof(TrayIcon).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded icon '{resourceName}' not found.");
        return new Icon(stream);
    }

    /// The app icon with a badge in the corner, the way OneDrive marks itself
    /// paused: two bars for a pause, a question mark for a device asking to
    /// be trusted. A question mark rather than an exclamation mark on
    /// purpose — an unknown device asking to pair is a question, not a fault,
    /// and `!` reads as something having gone wrong.
    ///
    /// Composed here rather than shipped as more .ico files so it cannot
    /// drift out of step with the real icon, and so the badge scales with
    /// whatever size the tray asks for. A solid blue disc with a light ring
    /// reads on both a light and a dark taskbar, which a flat glyph would
    /// not, and the white mark on it stays legible either way. The blue is
    /// macOS systemBlue, matching StatusIcon.swift and the Linux tray SVGs.
    ///
    /// Returns the unbadged icon if anything goes wrong: a tray icon missing
    /// its overlay is a cosmetic loss, no icon at all is a missing app.
    private static Icon? BuildBadgedIcon(Icon source, Badge badge)
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

                using var disc = new SolidBrush(Color.FromArgb(255, 10, 132, 255));
                using var ring = new Pen(Color.FromArgb(235, 255, 255, 255), Size * 0.055f);
                g.FillEllipse(disc, x, y, d, d);
                g.DrawEllipse(ring, x, y, d, d);

                float cx = x + d / 2f, cy = y + d / 2f;
                using var mark = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
                if (badge == Badge.Pause)
                {
                    // Two bars, centred in the disc.
                    float barW = d * 0.15f, barH = d * 0.42f, gap = d * 0.14f;
                    g.FillRectangle(mark, cx - gap / 2f - barW, cy - barH / 2f, barW, barH);
                    g.FillRectangle(mark, cx + gap / 2f, cy - barH / 2f, barW, barH);
                }
                else
                {
                    // A question mark, drawn as text: the glyph is a curve a
                    // few primitives cannot fake, and the badge is far too
                    // small for a hand-built path to read. Sized to the disc
                    // so it scales with Size like everything else here.
                    using var font = new Font(FontFamily.GenericSansSerif, d * 0.62f,
                                              FontStyle.Bold, GraphicsUnit.Pixel);
                    using var format = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                    };
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.DrawString("?", font, mark, new RectangleF(x, y, d, d), format);
                }
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
            Identity.Log($"TrayIcon: could not build the {badge} icon: {ex.GetType().Name}: {ex.Message}");
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
