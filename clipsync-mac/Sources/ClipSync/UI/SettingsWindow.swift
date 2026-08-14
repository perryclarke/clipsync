import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// Owns the settings window. Settings must be a separate window, not
/// popover content — the MenuBarExtra popover dismisses on deactivation,
/// so it cannot host a file picker or a sheet. Managed by hand (rather
/// than a `Settings` scene) so an accessory-policy app can reliably
/// activate and front it.
@MainActor
final class SettingsWindowController {
    static let shared = SettingsWindowController()
    private var window: NSWindow?

    func show(coordinator: AppCoordinator) {
        if window == nil {
            let content = SettingsView().environmentObject(coordinator)
            let hosting = NSHostingController(rootView: content)
            let w = NSWindow(contentViewController: hosting)
            w.title = "ClipSync Settings"
            w.styleMask = [.titled, .closable, .miniaturizable]
            w.isReleasedWhenClosed = false
            w.setContentSize(NSSize(width: 480, height: 460))
            w.center()
            window = w
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}

// MARK: - Settings content

struct SettingsView: View {
    @EnvironmentObject var coordinator: AppCoordinator
    @State private var showingPicker = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Excluded apps")
                .font(.headline)
            Text("Items copied while these apps are in the foreground are not sent to your other devices.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            Group {
                if coordinator.excludedApps.isEmpty {
                    VStack {
                        Spacer()
                        Text("No apps excluded. Everything you copy is synced.")
                            .foregroundStyle(.secondary)
                        Spacer()
                    }
                    .frame(maxWidth: .infinity)
                } else {
                    // The list scrolls inside itself; the add button below
                    // must never scroll away. The height shows a partial
                    // row so it is visible that scrolling reveals more.
                    ScrollView {
                        VStack(spacing: 0) {
                            ForEach(coordinator.excludedApps, id: \.self) { app in
                                ExcludedAppRow(app: app) {
                                    coordinator.removeExclusion(app)
                                }
                                Divider()
                            }
                        }
                    }
                }
            }
            .frame(height: 250)
            .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))

            HStack {
                Button("Add app…") { showingPicker = true }
                Spacer()
            }
        }
        .padding(16)
        .frame(width: 480)
        .sheet(isPresented: $showingPicker) {
            AppPickerView(
                alreadyExcluded: Set(coordinator.excludedApps.map(\.key))
            ) { identity in
                coordinator.addExclusion(identity)
            }
        }
    }
}

private struct ExcludedAppRow: View {
    let app: AppIdentity
    let onRemove: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            AppIconView(path: app.path)
            VStack(alignment: .leading, spacing: 2) {
                Text(app.displayName).fontWeight(.medium)
                Text(app.path ?? app.key)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer()
            Button("Remove", action: onRemove)
                .accessibilityLabel("Stop excluding \(app.displayName)")
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
    }
}

/// Bundle icon, or a generic placeholder when the app has no path or the
/// icon cannot be produced — the entry stays visible and removable either
/// way. Decorative only; hidden from the accessibility tree.
struct AppIconView: View {
    let path: String?

    var body: some View {
        Group {
            if let path {
                Image(nsImage: NSWorkspace.shared.icon(forFile: path))
                    .resizable()
            } else {
                Image(systemName: "app.dashed")
                    .resizable()
                    .foregroundStyle(.secondary)
            }
        }
        .frame(width: 24, height: 24)
        .accessibilityHidden(true)
    }
}

// MARK: - App picker

struct AppPickerView: View {
    let alreadyExcluded: Set<String>
    let onPick: (AppIdentity) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var search = ""
    @State private var apps: [InstalledApp]?     // nil = still loading
    @State private var loadFailed = false
    @State private var browseError: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Exclude an app")
                .font(.headline)

            TextField("Search apps", text: $search)
                .textFieldStyle(.roundedBorder)

            Group {
                if let apps {
                    pickerList(apps)
                } else {
                    VStack {
                        Spacer()
                        ProgressView()
                        Spacer()
                    }
                    .frame(maxWidth: .infinity)
                }
            }
            .frame(height: 300)
            .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))

            if let browseError {
                Text(browseError)
                    .font(.caption)
                    .foregroundStyle(.red)
            }

            HStack {
                Button("Browse…", action: browse)
                Spacer()
                Button("Cancel") { dismiss() }
                    .keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(width: 420)
        .task {
            // Enumeration takes hundreds of milliseconds — off the main
            // thread. The current search text is applied to the result at
            // render time, so anything typed while loading is preserved
            // rather than discarded.
            let found = await Task.detached(priority: .userInitiated) {
                InstalledApps.enumerate()
            }.value
            loadFailed = found.isEmpty
            apps = found
        }
    }

    @ViewBuilder
    private func pickerList(_ apps: [InstalledApp]) -> some View {
        // Three distinct empty states — enumeration failed, everything
        // already excluded, and search matched nothing — because showing
        // the wrong one sends the user down the wrong path.
        let selectable = apps.filter { !alreadyExcluded.contains($0.identity.key) }
        let filtered = search.isEmpty
            ? selectable
            : selectable.filter {
                $0.identity.displayName.localizedCaseInsensitiveContains(search)
                    || $0.identity.key.localizedCaseInsensitiveContains(search)
            }

        if loadFailed {
            centeredNote("Could not list installed apps. Use Browse… to pick one.")
        } else if selectable.isEmpty {
            centeredNote("Every installed app is already excluded.")
        } else if filtered.isEmpty {
            centeredNote("No apps match “\(search)”.")
        } else {
            ScrollView {
                VStack(spacing: 0) {
                    ForEach(filtered) { app in
                        Button {
                            onPick(app.identity)
                            dismiss()
                        } label: {
                            HStack(spacing: 10) {
                                AppIconView(path: app.identity.path)
                                Text(app.identity.displayName)
                                Spacer()
                            }
                            .contentShape(Rectangle())
                            .padding(.horizontal, 10)
                            .padding(.vertical, 6)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel("Exclude \(app.identity.displayName)")
                    }
                }
            }
        }
    }

    private func centeredNote(_ text: String) -> some View {
        VStack {
            Spacer()
            Text(text).foregroundStyle(.secondary)
            Spacer()
        }
        .frame(maxWidth: .infinity)
    }

    private func browse() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.applicationBundle]
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard let app = InstalledApps.read(url) else {
            browseError = "That app has no bundle identifier, so copies can’t be attributed to it."
            return
        }
        onPick(app.identity)
        dismiss()
    }
}
