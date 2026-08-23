using System;
using System.Collections.Generic;
using System.Text;
using ClipSync.Net;
using ClipSync.Platform;
using ClipSync.Security;
using ClipSync.Settings;

namespace ClipSync.Clipboard;

/// Turns local clipboard changes into ClipboardItems for the peers.
public sealed class ClipboardWatcher
{
    public Action<ClipboardItem>? OnLocalCopy;

    private readonly IClipboardBackend _backend;
    private readonly ClipboardWriter _writer;
    private readonly byte[] _localDid;
    private readonly AppSettings _settings;
    private readonly SelectionSource _source = new();
    private ulong _seq = 1;

    /// The local device id is passed in rather than read from
    /// Identity.Current: the watcher is otherwise pure logic over an offer,
    /// and reaching for a static makes it untestable without a keychain and
    /// a real identity on disk.
    public ClipboardWatcher(IClipboardBackend backend, ClipboardWriter writer, byte[] localDid,
                            AppSettings settings)
    {
        _backend = backend;
        _writer = writer;
        _localDid = localDid;
        _settings = settings;
    }

    public void Start() => _backend.Start(OnSelection);

    private void OnSelection(IClipboardOffer offer)
    {
        try
        {
            var copiedAt = DateTime.UtcNow;
            var available = new HashSet<string>(offer.MimeTypes, StringComparer.Ordinal);
            var formats = new List<ClipFormat>();

            foreach (var canonical in ClipboardFormats.Wanted)
            {
                foreach (var target in canonical.SourceTargets)
                {
                    if (!available.Contains(target)) continue;
                    var bytes = offer.Receive(target);

                    // An offered target that yields nothing is normal here,
                    // not a failure: X clients advertise types they cannot
                    // actually produce. Fall through to the next alias.
                    if (bytes is null || bytes.Length == 0) continue;

                    formats.Add(new ClipFormat(canonical.Mime, (ulong)bytes.Length, bytes, null));
                    break;
                }
            }

            AddCopiedFiles(offer, available, formats);

            if (formats.Count == 0) return;

            var item = new ClipboardItem(
                _seq++,
                _localDid,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                formats,
                FirstTextHint(formats));

            // Loop suppression first: this change may be the echo of our own
            // write coming back through the compositor's selection bridge.
            if (_writer.ConsumeRecentWrite(item.CanonicalHash()))
            {
                Identity.Log($"ClipboardWatcher: suppressed echo of our own write " +
                             $"({formats.Count} format(s))");
                return;
            }

            // Suppress transmission only. The item is already in this
            // machine's clipboard and stays there — excluding an app stops
            // it leaving, it does not stop you pasting locally.
            _source.Set(offer.SourceApp);
            if (SuppressionPolicy.ShouldSuppress(_source, _settings, copiedAt, out var excluded))
            {
                Identity.Log($"ClipboardWatcher: not sending item from {excluded!.DisplayName} " +
                             $"({formats.Count} format(s)) — app is excluded");
                return;
            }

            Identity.Log($"ClipboardWatcher: local copy seq={item.Seq} " +
                         $"[{string.Join(", ", formats.ConvertAll(f => $"{f.Mime} {f.Size}B"))}]" +
                         $" source={offer.SourceApp ?? "<unidentifiable>"}");

            OnLocalCopy?.Invoke(item);
        }
        catch (Exception ex)
        {
            Identity.Log($"ClipboardWatcher: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// Copied files travel as paths only, one format per file — the shape
    /// the Windows client produces from StorageItems. No platform transfers
    /// the contents, and every writer skips these on arrival.
    private static void AddCopiedFiles(IClipboardOffer offer, HashSet<string> available,
                                       List<ClipFormat> formats)
    {
        if (!available.Contains(ClipboardFormats.UriListTarget)) return;

        var raw = offer.Receive(ClipboardFormats.UriListTarget);
        if (raw is null || raw.Length == 0) return;

        foreach (var line in Encoding.UTF8.GetString(raw).Split('\n'))
        {
            if (ClipboardFormats.PathFromUriListLine(line) is not { } path) continue;
            var bytes = Encoding.UTF8.GetBytes(path);
            formats.Add(new ClipFormat(ClipboardFormats.FileUrlMime, (ulong)bytes.Length, bytes, null));
        }
    }

    private static string? FirstTextHint(List<ClipFormat> formats)
    {
        foreach (var f in formats)
        {
            if (!f.Mime.StartsWith("text/", StringComparison.Ordinal)) continue;
            if (f.Inline is not { } bytes) continue;
            var s = Encoding.UTF8.GetString(bytes);
            return s.Length <= 80 ? s : s[..80];
        }
        return null;
    }
}
