using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using ClipSync.Net;

namespace ClipSync.Clipboard;

/// Applies remote ClipboardItems to the local clipboard. Windows adds
/// each write to Win+V history automatically when Clipboard History is
/// enabled in Settings → System → Clipboard.
public sealed class ClipboardWriter
{
    private readonly object _lock = new();
    private readonly List<(byte[] Hash, DateTime Expiry)> _recent = new();

    public void Apply(ClipboardItem item)
    {
        var pkg = new DataPackage { RequestedOperation = DataPackageOperation.Copy };

        foreach (var f in item.Formats)
        {
            if (f.Inline is null) continue;
            switch (f.Mime)
            {
                case "text/plain;charset=utf-8":
                case "text/plain":
                    pkg.SetText(System.Text.Encoding.UTF8.GetString(f.Inline));
                    break;
                case "text/html":
                    pkg.SetHtmlFormat(System.Text.Encoding.UTF8.GetString(f.Inline));
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
                    break;
                default:
                    pkg.SetData(f.Mime, f.Inline);
                    break;
            }
        }

        lock (_lock)
        {
            _recent.Add((item.CanonicalHash(), DateTime.UtcNow.AddSeconds(5)));
        }

        WinClipboard.SetContent(pkg);
        WinClipboard.Flush();
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
