import Foundation

/// User preferences, stored as plain JSON at
/// `~/Library/Application Support/ClipSync/settings.json`, alongside
/// `trust.plist`. Same schema as the Windows side (`excludedApps` +
/// `pausedPeers`); macOS entries are `kind: "bundle"` keyed by bundle
/// identifier, and Windows' `exe`/`package` entries are ignored on load
/// rather than treated as errors.
///
/// Not a secret and deliberately hand-editable. A corrupt file degrades
/// to defaults rather than throwing, matching TrustStore.load(). Writes
/// are atomic (write-then-move) so a crash mid-write cannot leave a torn
/// file that silently vanishes the user's exclusions on next launch.
final class AppSettings {
    private static let currentVersion = 1

    private let url: URL
    private var excludedList: [AppIdentity]
    private var pausedList: [String]        // lowercase DID hex, de-duplicated
    private let lock = NSLock()

    private init(url: URL, excluded: [AppIdentity], pausedPeers: [String]) {
        self.url = url
        self.excludedList = excluded
        self.pausedList = pausedPeers
    }

    static func defaultURL() -> URL {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory,
                                                  in: .userDomainMask).first!
        return appSupport
            .appendingPathComponent("ClipSync", isDirectory: true)
            .appendingPathComponent("settings.json")
    }

    static func load() -> AppSettings { load(url: defaultURL()) }

    static func load(url: URL) -> AppSettings {
        var excluded: [AppIdentity] = []
        var paused: [String] = []
        if FileManager.default.fileExists(atPath: url.path) {
            do {
                let data = try Data(contentsOf: url)
                let model = try JSONDecoder().decode(FileModel.self, from: data)
                for e in model.excludedApps ?? [] {
                    // Unknown kinds (Windows' "exe"/"package", or anything a
                    // future version invents) are skipped, not errors.
                    guard e.kind?.lowercased() == AppIdentity.Kind.bundle.rawValue,
                          let key = e.key,
                          let id = AppIdentity(kind: .bundle, key: key,
                                               displayName: e.name ?? key, path: e.path)
                    else { continue }
                    if !excluded.contains(id) { excluded.append(id) }
                }
                for did in model.pausedPeers ?? [] {
                    // Normalise to lowercase, drop blanks, de-duplicate. An
                    // entry naming a peer this device has never met is kept
                    // deliberately, so pausing a machine, forgetting it and
                    // meeting it again does not silently un-pause it.
                    guard let key = AppSettings.normaliseDid(did) else { continue }
                    if !paused.contains(key) { paused.append(key) }
                }
            } catch {
                // Corrupt or unreadable: start empty rather than crashing.
                NSLog("AppSettings: could not read %@: %@; using defaults",
                      url.path, String(describing: error))
                excluded = []
                paused = []
            }
        }
        return AppSettings(url: url, excluded: excluded, pausedPeers: paused)
    }

    // MARK: Excluded apps

    var excluded: [AppIdentity] {
        lock.lock(); defer { lock.unlock() }
        return excludedList
    }

    func isExcluded(_ app: AppIdentity) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return excludedList.contains(app)
    }

    func add(_ app: AppIdentity) {
        lock.lock(); defer { lock.unlock() }
        guard !excludedList.contains(app) else { return }
        excludedList.append(app)
        persistLocked()
    }

    func remove(_ app: AppIdentity) {
        lock.lock(); defer { lock.unlock() }
        let before = excludedList.count
        excludedList.removeAll { $0 == app }
        guard excludedList.count != before else { return }
        persistLocked()
    }

    // MARK: Paused peers

    /// Peers this device does not send to, by lowercase DID hex.
    ///
    /// Only the per-peer pause lives here. A global pause is deliberately
    /// not persisted: it means "not right now", and a restart that
    /// silently left syncing off would be a bad surprise.
    var pausedPeers: [String] {
        lock.lock(); defer { lock.unlock() }
        return pausedList
    }

    func isPeerPaused(_ didHex: String) -> Bool {
        guard let key = AppSettings.normaliseDid(didHex) else { return false }
        lock.lock(); defer { lock.unlock() }
        return pausedList.contains(key)
    }

    func setPeerPaused(_ didHex: String, paused: Bool) {
        guard let key = AppSettings.normaliseDid(didHex) else { return }
        lock.lock(); defer { lock.unlock() }
        if paused {
            guard !pausedList.contains(key) else { return }
            pausedList.append(key)
        } else {
            let before = pausedList.count
            pausedList.removeAll { $0 == key }
            guard pausedList.count != before else { return }
        }
        persistLocked()
    }

    /// DIDs compare lowercase; a blank one is not a peer.
    static func normaliseDid(_ didHex: String?) -> String? {
        guard let didHex else { return nil }
        let trimmed = didHex.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return trimmed.isEmpty ? nil : trimmed
    }

    // MARK: Persistence

    /// Caller holds `lock`. Writes the whole model — both features share
    /// this file, and writing everything each time is what guarantees
    /// neither clobbers the other.
    private func persistLocked() {
        let model = FileModel(
            version: AppSettings.currentVersion,
            excludedApps: excludedList.map {
                FileModel.Entry(kind: $0.kind.rawValue, key: $0.key,
                                name: $0.displayName, path: $0.path)
            },
            pausedPeers: pausedList
        )
        do {
            let dir = url.deletingLastPathComponent()
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            let data = try encoder.encode(model)
            // .atomic is write-to-temp-then-rename, the same guarantee the
            // Windows side gets from File.Move.
            try data.write(to: url, options: .atomic)
        } catch {
            // In-memory state keeps working for this session.
            NSLog("AppSettings: could not write %@: %@", url.path, String(describing: error))
        }
    }

    /// The on-disk shape, shared with Windows. Field names are camelCase
    /// to match; everything optional so a partial or foreign file decodes.
    private struct FileModel: Codable {
        var version: Int?       // optional on decode: a file missing it still loads
        var excludedApps: [Entry]?
        var pausedPeers: [String]?

        init(version: Int, excludedApps: [Entry], pausedPeers: [String]) {
            self.version = version
            self.excludedApps = excludedApps
            self.pausedPeers = pausedPeers
        }

        struct Entry: Codable {
            var kind: String?
            var key: String?
            var name: String?
            var path: String?
        }
    }
}
