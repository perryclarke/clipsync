import Foundation

/// Names an application for exclusion purposes.
///
/// Equality is deliberately on kind+key only: displayName and path are
/// presentation data, so a renamed or relocated app still matches a saved
/// exclusion. Mirrors the Windows AppIdentity, where the same rule holds
/// for exe names and package family names.
struct AppIdentity: Equatable, Hashable {
    enum Kind: String {
        /// macOS application, keyed by bundle identifier. The only kind
        /// this platform produces; `exe`/`package` entries in a shared
        /// settings file are Windows-owned and ignored here.
        case bundle
    }

    let kind: Kind
    /// Normalised match key — the bundle identifier, trimmed and
    /// lowercased. The picker and the foreground resolver must both go
    /// through `normalise` so the stored key always equals the key the
    /// matcher produces (handoff §2.2).
    let key: String
    let displayName: String
    /// Path of the .app the user originally picked; presentation only.
    let path: String?

    init?(kind: Kind, key: String, displayName: String, path: String? = nil) {
        guard let normalised = AppIdentity.normalise(key) else { return nil }
        self.kind = kind
        self.key = normalised
        self.displayName = displayName
        self.path = path
    }

    /// One normalisation for both the picker and the resolver, by
    /// construction — bundle identifiers compare case-insensitively.
    static func normalise(_ key: String) -> String? {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return trimmed.isEmpty ? nil : trimmed
    }

    static func == (lhs: AppIdentity, rhs: AppIdentity) -> Bool {
        lhs.kind == rhs.kind && lhs.key == rhs.key
    }

    func hash(into hasher: inout Hasher) {
        hasher.combine(kind)
        hasher.combine(key)
    }
}
