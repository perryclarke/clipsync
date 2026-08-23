using System;
using System.Collections.Generic;
using System.IO;
using ClipSync.Security;

namespace ClipSync.Ui;

/// The tray icon pixmaps served over the SNI IconPixmap property.
///
/// The other two platforms compose their status icon at runtime — the
/// clipboard glyph with a wifi or pause badge. No GNOME stock icon looks
/// like that, so the same design ships as SVGs (icons/tray-*.svg, drawn to
/// match StatusIcon.swift) rendered through gdk-pixbuf, whose librsvg
/// loader is in the Ubuntu default install. If rendering fails the tray
/// falls back to a stock icon name rather than showing nothing.
internal static class TrayPixmaps
{
    // 22 is what the panel actually draws; 44 is the 2x variant the host
    // picks on a HiDPI display.
    private static readonly int[] Sizes = [22, 44];

    public static IReadOnlyList<(int Size, byte[] Argb)>? Active { get; }
    public static IReadOnlyList<(int Size, byte[] Argb)>? Paused { get; }

    /// Force the static initializer now, on the caller's thread. Called
    /// from Program.Main before discovery starts: GirCore's module/type
    /// registration is not safe to race from two threads, and the first
    /// peer change starts the GTK thread (Adw.Application.New does its own
    /// registration). Loading here, first, serializes the two.
    public static void EnsureLoaded() { }

    static TrayPixmaps()
    {
        try
        {
            // Registers the DllImport resolvers mapping "GLib" etc. to the
            // real sonames. The window path gets this from Adw; this path
            // may run first, or without any window at all.
            GLib.Module.Initialize();
            GObject.Module.Initialize();
            GdkPixbuf.Module.Initialize();
        }
        catch (Exception ex)
        {
            Identity.Log($"Tray: GirCore init failed ({ex.Message})");
        }
        Active = TryRender("tray-active.svg");
        Paused = TryRender("tray-paused.svg");
    }

    private static IReadOnlyList<(int, byte[])>? TryRender(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "icons", file);
        try
        {
            var rendered = new List<(int, byte[])>();
            foreach (var size in Sizes)
            {
                using var pixbuf = GdkPixbuf.Pixbuf.NewFromFileAtSize(path, size, size)
                    ?? throw new InvalidOperationException("pixbuf load returned null");
                int width = pixbuf.GetWidth(), height = pixbuf.GetHeight();
                int rowstride = pixbuf.GetRowstride(), channels = pixbuf.GetNChannels();

                // The last row is not stride-padded, hence the two terms.
                var pixels = new byte[rowstride * (height - 1) + width * channels];
                System.Runtime.InteropServices.Marshal.Copy(pixbuf.ReadPixels(), pixels, 0, pixels.Length);
                rendered.Add((size, ToArgbNetworkOrder(pixels, width, height, rowstride, channels)));
            }
            Identity.Log($"Tray: rendered {file} at {string.Join(", ", Sizes)} px");
            return rendered;
        }
        catch (Exception ex)
        {
            Identity.Log($"Tray: cannot render {path} ({ex.GetType().Name}: {ex.Message}) — using a stock icon");
            return null;
        }
    }

    /// gdk-pixbuf hands back RGBA (or RGB) rows with padding; the SNI wire
    /// format wants tightly packed ARGB32 in network byte order.
    internal static byte[] ToArgbNetworkOrder(ReadOnlySpan<byte> pixels,
                                              int width, int height,
                                              int rowstride, int channels)
    {
        var argb = new byte[width * height * 4];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * rowstride, width * channels);
            for (var x = 0; x < width; x++)
            {
                var p = x * channels;
                argb[o++] = channels == 4 ? row[p + 3] : (byte)255;
                argb[o++] = row[p];
                argb[o++] = row[p + 1];
                argb[o++] = row[p + 2];
            }
        }
        return argb;
    }
}
