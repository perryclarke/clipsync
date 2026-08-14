import AppKit
import Foundation

/// One entry in the app picker.
struct InstalledApp: Identifiable {
    var id: String { identity.key }
    let identity: AppIdentity
    let url: URL
}

/// Enumerates installed applications by scanning the standard application
/// folders for `.app` bundles. Slow enough (hundreds of bundles) that it
/// must run off the main thread; icons are fetched lazily per-row on the
/// main thread instead of here, because NSImage/NSWorkspace icon work is
/// cheap per item and safest with main-thread affinity.
enum InstalledApps {

    static let roots: [URL] = {
        var urls = [
            URL(fileURLWithPath: "/Applications", isDirectory: true),
            URL(fileURLWithPath: "/System/Applications", isDirectory: true),
        ]
        let home = FileManager.default.homeDirectoryForCurrentUser
        urls.append(home.appendingPathComponent("Applications", isDirectory: true))
        return urls
    }()

    /// Scan for .app bundles, one row per bundle identifier. Apps without
    /// a bundle identifier are skipped (they could never be matched at
    /// copy time), as is ClipSync itself.
    static func enumerate() -> [InstalledApp] {
        var byKey: [String: InstalledApp] = [:]
        let ownKey = Bundle.main.bundleIdentifier.flatMap(AppIdentity.normalise)
        for root in roots {
            scan(root, depth: 0, into: &byKey, skipping: ownKey)
        }
        return byKey.values.sorted {
            $0.identity.displayName.localizedCaseInsensitiveCompare($1.identity.displayName) == .orderedAscending
        }
    }

    private static func scan(_ dir: URL, depth: Int,
                             into byKey: inout [String: InstalledApp],
                             skipping ownKey: String?) {
        guard depth <= 2 else { return }
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(
            at: dir,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        ) else { return }

        for url in entries {
            if url.pathExtension == "app" {
                guard let app = read(url), app.identity.key != ownKey else { continue }
                // One bundle identifier, one row — the same app installed
                // in two folders is still one app.
                if byKey[app.identity.key] == nil { byKey[app.identity.key] = app }
            } else if (try? url.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory == true {
                // Recurse into e.g. /Applications/Utilities, but not into
                // bundles and not without bound.
                scan(url, depth: depth + 1, into: &byKey, skipping: ownKey)
            }
        }
    }

    /// Read a bundle into an identity, going through the same
    /// normalisation the foreground resolver uses — the picker's stored
    /// key must equal the key the matcher produces (handoff §2.2).
    static func read(_ url: URL) -> InstalledApp? {
        guard let bundle = Bundle(url: url),
              let bundleId = bundle.bundleIdentifier else { return nil }
        let name = (bundle.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String)
            ?? (bundle.object(forInfoDictionaryKey: "CFBundleName") as? String)
            ?? url.deletingPathExtension().lastPathComponent
        guard let identity = AppIdentity(kind: .bundle, key: bundleId,
                                         displayName: name, path: url.path)
        else { return nil }
        return InstalledApp(identity: identity, url: url)
    }
}
