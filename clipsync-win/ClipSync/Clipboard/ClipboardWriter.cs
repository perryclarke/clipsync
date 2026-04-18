using System;
using System.Collections.Generic;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using ClipSync.Net;
using ClipSync.Security;

namespace ClipSync.Clipboard;

/// Applies remote ClipboardItems to the local clipboard.
public sealed class ClipboardWriter
{
    private readonly object _lock = new();
    private readonly List<(byte[] Hash, DateTime Expiry)> _recent = new();

    public void Apply(ClipboardItem item)
    {
        try
        {
            var pkg = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            bool hasContent = false;

            foreach (var f in item.Formats)
            {
                if (f.Inline is null) continue;
                switch (f.Mime)
                {
                    case "text/plain;charset=utf-8":
                    case "text/plain":
                        pkg.SetText(System.Text.Encoding.UTF8.GetString(f.Inline));
                        hasContent = true;
                        break;
                    case "text/html":
                        pkg.SetHtmlFormat(System.Text.Encoding.UTF8.GetString(f.Inline));
                        hasContent = true;
                        break;
                    case "image/png":
                    case "image/jpeg":
                        var ms = new InMemoryRandomAccessStream();
                        using (var writer = new DataWriter(ms.GetOutputStreamAt(0)))
                        {
                            writer.WriteBytes(f.Inline);
                            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                        }
                        pkg.SetBitmap(RandomAccessStreamReference.CreateFromStream(ms));
                        hasContent = true;
                        break;
                    default:
                        // Skip unsupported MIME types — DataPackage.SetData
                        // only accepts WinRT-compatible values.
                        Identity.Log($"ClipboardWriter: skipping unsupported mime={f.Mime}");
                        break;
                }
            }

            if (!hasContent) return;

            lock (_lock)
            {
                _recent.Add((item.CanonicalHash(), DateTime.UtcNow.AddSeconds(5)));
            }

            // Clipboard APIs must be called on the UI thread.
            App.UIDispatcher.TryEnqueue(() =>
            {
                try
                {
                    WinClipboard.SetContent(pkg);
                    WinClipboard.Flush();
                    Identity.Log($"ClipboardWriter: applied item seq={item.Seq} hint={item.Hint}");
                }
                catch (Exception ex2)
                {
                    Identity.Log($"ClipboardWriter dispatch error: {ex2.GetType().Name}: {ex2.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Identity.Log($"ClipboardWriter.Apply error: {ex.GetType().Name}: {ex.Message} HResult=0x{ex.HResult:X8}");
        }
    }

    public bool ConsumeRecentWrite(byte[] hash)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _recent.RemoveAll(r => r.Expiry < now);
            var idx = _recent.FindIndex(r => r.Hash.SequenceEqual(hash));
            if (idx < 0) return false;
            _recent.RemoveAt(idx);
            return true;
        }
    }
}
