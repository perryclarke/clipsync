import Foundation

/// The app's marketing version, from the bundle's Info.plist. "dev" when
/// run outside an app bundle (a bare `swift run` has no Info.plist).
enum AppVersion {
    static let current =
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev"
}
