using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using ClipSync.Net;
using ClipSync.Security;

namespace ClipSync.Clipboard;

/// Listens for Clipboard.ContentChanged and emits a ClipboardItem for
/// each local copy. The writer tells us when we ourselves just applied a
/// remote item so we can suppress the resulting echo.
public sealed class ClipboardWatcher
{
    public Action<ClipboardItem>? OnLocalCopy;

    private readonly ClipboardWriter _writer;
    private ulong _seq = 1;

    public ClipboardWatcher(ClipboardWriter writer) { _writer = writer; }

    public void Start()
    {
        WinClipboard.ContentChanged += async (_, _) => await OnChangedAsync();
    }

    private async Task OnChangedAsync()
    {
        try
        {
            var view = WinClipboard.GetContent();
            var formats = new List<ClipFormat>();

            if (view.Contains(StandardDataFormats.Text))
            {
                var s = await view.GetTextAsync();
                var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                formats.Add(new ClipFormat("text/plain;charset=utf-8", (ulong)bytes.Length, bytes, null));
            }
            if (view.Contains(StandardDataFormats.Html))
            {
                var s = await view.GetHtmlFormatAsync();
                var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                formats.Add(new ClipFormat("text/html", (ulong)bytes.Length, bytes, null));
            }
            if (view.Contains(StandardDataFormats.Bitmap))
            {
                var stream = await view.GetBitmapAsync();
                using var raw = await stream.OpenReadAsync();
                var buf = new byte[raw.Size];
                var reader = new Windows.Storage.Streams.DataReader(raw);
                await reader.LoadAsync((uint)raw.Size);
                reader.ReadBytes(buf);
                formats.Add(new ClipFormat("image/png", (ulong)buf.Length, buf, null));
            }
            if (view.Contains(StandardDataFormats.StorageItems))
            {
                var items = await view.GetStorageItemsAsync();
                foreach (var it in items)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(it.Path);
                    formats.Add(new ClipFormat("application/x-file-url", (ulong)bytes.Length, bytes, null));
                }
            }

            if (formats.Count == 0) return;

            var item = new ClipboardItem(
                _seq++,
                Identity.Current.Did,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                formats,
                FirstTextHint(formats));

            if (_writer.ConsumeRecentWrite(item.CanonicalHash())) return;
            OnLocalCopy?.Invoke(item);
        }
        catch { /* ignore; clipboard APIs can throw transiently */ }
    }

    private static string? FirstTextHint(List<ClipFormat> fs)
    {
        foreach (var f in fs)
        {
            if (f.Mime.StartsWith("text/") && f.Inline is { } b)
            {
                var s = System.Text.Encoding.UTF8.GetString(b);
                return s.Length <= 80 ? s : s.Substring(0, 80);
            }
        }
        return null;
    }
}
