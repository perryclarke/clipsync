using System;
using System.Collections.Generic;

namespace ClipSync.Clipboard;

/// Translation between the X selection's target list and the small set of
/// MIME types the protocol actually carries.
///
/// The X clipboard is far messier than the mac or Windows one. A single
/// image copied in Firefox offered twenty targets totalling ~1.4 MB for
/// about 300 KB of distinct content: ten of them returned zero bytes,
/// `image/bmp` / `image/x-bmp` / `image/x-MS-bmp` were the same 576 KB
/// under three names, and `audio/x-riff` held the WebP data because WebP is
/// a RIFF container. Mirroring that list onto the wire would send peers
/// several copies of one screenshot plus a pile of empty formats.
///
/// So the watcher does not mirror the target list. It asks for a fixed,
/// ordered set of canonical formats — the same four the Windows client
/// produces — and takes the first source target that yields bytes for each.
/// Duplicates, empties and mislabelled types are excluded by construction
/// rather than filtered after the fact.
internal static class ClipboardFormats
{
    /// A canonical wire format, and the X targets that can supply it in
    /// preference order.
    internal sealed record Canonical(string Mime, string[] SourceTargets);

    /// Ordered: the first text format found also supplies the UI hint.
    public static readonly Canonical[] Wanted =
    [
        new("text/plain;charset=utf-8",
            ["text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "STRING"]),
        new("text/html", ["text/html"]),
        new("image/png", ["image/png"]),
    ];

    /// The X target carrying copied files, handled separately because one
    /// target expands into several wire formats (one per file).
    public const string UriListTarget = "text/uri-list";

    /// What the other two clients call a copied file. Contents are not
    /// transferred on any platform — only the path travels.
    public const string FileUrlMime = "application/x-file-url";

    /// Targets to offer for a canonical MIME when we own the selection.
    ///
    /// The aliases matter: plenty of X clients ask for `UTF8_STRING` or
    /// `STRING` and never look for a MIME-typed target, so offering only
    /// `text/plain;charset=utf-8` would make pasting fail in exactly the
    /// older applications most likely to be doing the pasting.
    public static IReadOnlyList<string> OfferAliases(string mime) => mime switch
    {
        "text/plain;charset=utf-8" or "text/plain" =>
            ["text/plain;charset=utf-8", "text/plain", "UTF8_STRING", "STRING", "TEXT"],
        "text/html" => ["text/html"],
        "image/png" => ["image/png"],
        "image/jpeg" => ["image/jpeg"],
        _ => [mime],
    };

    /// Decode one `text/uri-list` line into a filesystem path, or null if
    /// it is a comment, blank, or not a local file.
    public static string? PathFromUriListLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith('#')) return null;
        if (!line.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        try { return Uri.UnescapeDataString(new Uri(line).AbsolutePath); }
        catch { return null; }
    }
}
