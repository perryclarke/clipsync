using System;
using ClipSync.Settings;

namespace ClipSync.Clipboard;

/// Names the app behind the current clipboard change, for the excluded-apps
/// decision.
///
/// The other two platforms ask "which app was in front when this was
/// copied?" and answer from a ring of recent foreground changes, because
/// their clipboard APIs do not say who wrote to it. X does say: the
/// selection has an owner, and that owner resolves to a real window. So
/// this is a one-slot holder set from the offer immediately before the
/// decision is made, rather than a time-indexed history.
///
/// It still implements IForegroundSource so the decision itself stays in
/// the shared, tested SuppressionPolicy rather than being reimplemented
/// here with subtly different fail-open behaviour.
internal sealed class SelectionSource : IForegroundSource
{
    private AppIdentity? _current;

    /// Entries are stored as AppKind.Exe keyed on WM_CLASS.
    ///
    /// There is no AppKind for an X window class, and adding one would mean
    /// editing AppIdentity — a file linked from clipsync-win, which must not
    /// change. Exe is the closest fit: its key normalisation lowercases and
    /// takes the file-name portion, which leaves a WM_CLASS untouched since
    /// those never contain path separators. The settings file therefore
    /// stays schema-compatible, and the mac and Windows clients read these
    /// entries happily and simply never match them — which is correct, as
    /// they name Linux applications.
    public void Set(string? wmClass)
    {
        _current = string.IsNullOrWhiteSpace(wmClass)
            ? null
            : new AppIdentity(AppKind.Exe, wmClass, wmClass);
    }

    /// The timestamp is ignored: the source was captured from the very
    /// change being judged, so there is no window to look back through.
    public AppIdentity? AppAt(DateTime utc) => _current;
}
