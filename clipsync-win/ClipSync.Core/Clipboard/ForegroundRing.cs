using System;
using System.Collections.Generic;
using ClipSync.Settings;

namespace ClipSync.Clipboard;

/// A short, bounded history of which app was in the foreground and when.
///
/// Clipboard change notifications arrive asynchronously, so by the time
/// the watcher has an item the user may already have switched apps.
/// Recording transitions as they happen lets the watcher ask what was in
/// front at the moment of the copy rather than at the moment of handling.
public sealed class ForegroundRing
{
    public const int MaxEntries = 16;
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    private readonly List<Entry> _entries = new();
    private readonly object _lock = new();

    private readonly record struct Entry(DateTime At, AppIdentity? App);

    public void Record(DateTime atUtc, AppIdentity? app)
    {
        lock (_lock)
        {
            _entries.Add(new Entry(atUtc, app));
            Trim(atUtc);
        }
    }

    /// The app whose interval contains `utc`, or null if `utc` predates
    /// everything retained (which the caller treats as fail-open).
    public AppIdentity? AppAt(DateTime utc)
    {
        lock (_lock)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
                if (_entries[i].At <= utc) return _entries[i].App;
            return null;
        }
    }

    /// Caller holds _lock. Always leaves at least the newest entry, so a
    /// user who has sat in one app for hours still resolves.
    private void Trim(DateTime nowUtc)
    {
        // Find the newest expired entry, then remove everything up to and
        // including it in one shot — removing inside the scan would
        // invalidate the indices we are still iterating over.
        var lastExpired = -1;
        for (var i = 0; i < _entries.Count - 1; i++)
            if (nowUtc - _entries[i].At > MaxAge) lastExpired = i;
        if (lastExpired >= 0) _entries.RemoveRange(0, lastExpired + 1);

        while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
    }
}
