using System;
using System.Collections.Generic;
using System.Linq;
using ClipSync.Net;
using ClipSync.Platform;
using ClipSync.Security;

namespace ClipSync.Clipboard;

/// Applies ClipboardItems from peers to the local clipboard.
public sealed class ClipboardWriter
{
    private readonly IClipboardBackend _backend;
    private readonly object _lock = new();
    private readonly List<(byte[] Hash, DateTime Expiry)> _recent = new();

    /// How long a write stays recognisable as ours. Matches the 5 s in
    /// PROTOCOL.md §8 and the other two clients.
    private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(5);

    public ClipboardWriter(IClipboardBackend backend)
    {
        _backend = backend;
    }

    public void Apply(ClipboardItem item)
    {
        try
        {
            var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var written = new List<ClipFormat>();

            foreach (var f in item.Formats)
            {
                if (f.Inline is not { } bytes) continue;      // never materialised

                if (f.Mime == ClipboardFormats.FileUrlMime)
                {
                    // Only the path was sent, and a path from another
                    // machine means nothing here. Skipped on every platform.
                    Identity.Log($"ClipboardWriter: skipping {f.Mime} (file contents are not transferred)");
                    continue;
                }

                var aliases = ClipboardFormats.OfferAliases(f.Mime);
                if (aliases.Count == 0) continue;
                foreach (var alias in aliases) data[alias] = bytes;
                written.Add(f);
            }

            if (written.Count == 0)
            {
                Identity.Log($"ClipboardWriter: nothing writable in item seq={item.Seq}");
                return;
            }

            // Stamp before writing, not after: the change event races the
            // return of SetSelection, and a marker added afterwards can lose
            // that race and let our own write echo back to the peer.
            //
            // Two hashes are stamped. The item's own hash covers the simple
            // case, but we may have dropped formats the local clipboard
            // cannot hold (a file path, say) — the watcher will then rebuild
            // a *smaller* item whose hash differs from the original. So the
            // reduced form is stamped too, which is what actually comes back.
            StampRecent(item.CanonicalHash());
            if (written.Count != item.Formats.Count)
            {
                StampRecent(new ClipboardItem(item.Seq, item.OriginDid, item.TsMs,
                                              written, item.Hint).CanonicalHash());
            }

            _backend.SetSelection(data);
            Identity.Log($"ClipboardWriter: applied item seq={item.Seq} " +
                         $"[{string.Join(", ", written.Select(f => f.Mime))}]");
        }
        catch (Exception ex)
        {
            Identity.Log($"ClipboardWriter.Apply: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void StampRecent(byte[] hash)
    {
        lock (_lock) _recent.Add((hash, DateTime.UtcNow + RecentWindow));
    }

    /// True if `hash` is a clipboard change this writer caused. Consuming
    /// removes it, so a genuine re-copy of the same content moments later
    /// is still transmitted.
    public bool ConsumeRecentWrite(byte[] hash)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _recent.RemoveAll(r => r.Expiry < now);
            var index = _recent.FindIndex(r => r.Hash.SequenceEqual(hash));
            if (index < 0) return false;
            _recent.RemoveAt(index);
            return true;
        }
    }
}
