import Foundation

/// A short, bounded history of which app was frontmost and when.
///
/// `PasteboardWatcher` polls `changeCount` every 200 ms, so the copy time
/// is only known to within that interval. The suppression rule therefore
/// needs "which apps held focus between t1 and t2", not just "which app
/// held focus at t" — a different query from the Windows ring, and the
/// reason `appsIn(_:)` exists.
///
/// Semantics mirror the Windows `ForegroundRing`: each entry owns the
/// half-open interval `[its timestamp, the next entry's timestamp)`, the
/// newest entry runs to infinity, and a timestamp falling exactly on a
/// transition resolves to the newly-activated app. Bounded in both size
/// and age; anything evicted reads back as unknown, which the decision
/// treats as fail-open (transmit).
final class ForegroundRing {
    static let maxEntries = 16
    static let maxAge: TimeInterval = 120

    private struct Entry {
        let at: Date
        let app: AppIdentity?    // nil = frontmost app could not be resolved
    }

    private var entries: [Entry] = []
    private let lock = NSLock()

    func record(at: Date, app: AppIdentity?) {
        lock.lock(); defer { lock.unlock() }
        entries.append(Entry(at: at, app: app))
        // Trim as a filter, not an in-place walk (the Windows first draft
        // mutated the list while iterating over it). Always keep the newest
        // entry, so a user who has sat in one app for hours still resolves.
        let cutoff = at.addingTimeInterval(-ForegroundRing.maxAge)
        if entries.count > 1 {
            let newest = entries.removeLast()
            entries = entries.filter { $0.at >= cutoff }
            entries.append(newest)
        }
        while entries.count > ForegroundRing.maxEntries {
            entries.removeFirst()
        }
    }

    /// The app whose interval contains `t`, or nil if `t` predates
    /// everything retained (fail open) or the entry recorded no identity.
    func appAt(_ t: Date) -> AppIdentity? {
        lock.lock(); defer { lock.unlock() }
        for entry in entries.reversed() where entry.at <= t {
            return entry.app
        }
        return nil
    }

    /// Every resolved app that held focus at any point in `(start, end]` —
    /// the polling window preceding a tick. An entry whose ownership
    /// interval `[at, next.at)` overlaps the window is included. Portions
    /// of the window older than the oldest retained entry are unknown and
    /// contribute nothing, which the caller treats as fail-open.
    func appsIn(start: Date, end: Date) -> [AppIdentity] {
        lock.lock(); defer { lock.unlock() }
        var found: [AppIdentity] = []
        for (i, entry) in entries.enumerated() {
            let intervalEnd = i + 1 < entries.count ? entries[i + 1].at : Date.distantFuture
            // Overlap of [entry.at, intervalEnd) with (start, end]:
            guard entry.at <= end, intervalEnd > start else { continue }
            if let app = entry.app, !found.contains(app) {
                found.append(app)
            }
        }
        return found
    }
}
