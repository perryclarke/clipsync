import Foundation

/// The one decision the excluded-apps feature exists to make: given the
/// polling window a copy fell in, may the item be transmitted to peers?
///
/// Lives apart from the clipboard plumbing because it is the assertion
/// the feature is judged on and it must be directly testable — and
/// because on Windows the same decision, buried inline, once failed
/// *closed* through a bare catch without anyone noticing. The
/// unresolvable case here is an explicit branch, not an exception path.
enum SuppressionPolicy {

    struct Decision {
        /// True when the item must NOT be transmitted.
        let suppress: Bool
        /// The excluded app that triggered suppression, or nil when
        /// transmitting. Presentation/logging data only — never content.
        let source: AppIdentity?
    }

    /// The macOS rule (deliberately stricter than Windows, where the
    /// exact copy timestamp is known): if **any** app that held focus
    /// during the polling window `(windowStart, windowEnd]` is excluded,
    /// suppress. The alternative — whoever is frontmost at tick time —
    /// leaks on a fast copy-then-switch, which is exactly the sequence of
    /// copying a password and switching to the app that needs it.
    ///
    /// Fails open in every uncertain case: an empty ring, an unresolved
    /// app, or a window predating everything retained all yield
    /// "transmit". Silent non-delivery is a worse failure than a rare
    /// miss.
    static func decide(ring: ForegroundRing, settings: AppSettings,
                       windowStart: Date, windowEnd: Date) -> Decision {
        let candidates = ring.appsIn(start: windowStart, end: windowEnd)
        for app in candidates where settings.isExcluded(app) {
            return Decision(suppress: true, source: app)
        }
        return Decision(suppress: false, source: nil)
    }
}
