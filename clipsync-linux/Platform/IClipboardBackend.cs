using System;
using System.Collections.Generic;

namespace ClipSync.Platform;

/// A clipboard offer: what is on the clipboard, and how to read it.
///
/// An offer is only valid for the duration of the callback that delivered
/// it. Backends serve reads on their own thread against live protocol
/// objects — an X selection conversion, a Wayland fd — and neither survives
/// being used later or from elsewhere.
public interface IClipboardOffer
{
    /// MIME types on offer. May contain duplicates, aliases for identical
    /// bytes, types that yield nothing, and outright wrong labels; see
    /// ClipboardWatcher, which is where that is cleaned up.
    IReadOnlyList<string> MimeTypes { get; }

    /// Read one type. Null if the owner refuses or the read times out —
    /// ordinary, not exceptional.
    byte[]? Receive(string mimeType);

    /// Best-effort identity of the application that made the copy, or null
    /// when it cannot be determined. On this stack that means X11/XWayland
    /// apps only; a Wayland-native copy arrives owned by the compositor's
    /// bridge and is never identifiable.
    string? SourceApp { get; }
}

/// Reading and writing the system clipboard.
///
/// Shaped after `ext-data-control-v1` rather than after X11: that protocol
/// is the more constrained of the two (an offer announces MIME types, then
/// each type is read separately over an fd), and an interface built around
/// X11 instead would leak INCR, target atoms and selection-ownership
/// semantics into every future backend. X11 adapts to this shape easily;
/// the reverse would not.
public interface IClipboardBackend : IDisposable
{
    /// Short stable name, used in logs and the CLIPSYNC_BACKEND_CLIPBOARD
    /// override.
    string Name { get; }

    /// Whether this backend can be used in the current session.
    bool IsAvailable();

    /// Begin watching. `onSelection` is raised for every clipboard change,
    /// including ones this process caused — deduplicating those is the
    /// watcher's job, via the shared SuppressionPolicy.
    void Start(Action<IClipboardOffer> onSelection);

    /// Take ownership of the clipboard and serve `data` until something
    /// else claims it.
    void SetSelection(IReadOnlyDictionary<string, byte[]> data);
}
