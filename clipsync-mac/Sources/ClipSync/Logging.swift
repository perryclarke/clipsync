import Foundation

/// Diagnostic logging, mirroring the Windows `Identity.Log`
/// (clipsync-win/ClipSync/Security/Identity.cs) so a log from either
/// platform reads the same: same gate, same rotation, same line format.
///
/// Two sinks, gated differently on purpose:
///
/// - **stderr, always.** Running the binary directly and reading stderr is
///   the documented way to debug the Mac, and an `open`-launched bundle's
///   NSLog does not surface via `log stream`. Gating this would take away
///   the one channel that has always worked.
/// - **The log file, only when debug logging is on.** This is the part that
///   matches Windows, and what the Debug section in Settings switches.
///
/// Like Windows, what goes in must be network and protocol metadata only —
/// never clipboard content, never key material.
enum Log {
    /// Rotate at the same threshold as Windows, to the same `.1` sibling.
    static let maxBytes = 5_000_000

    /// Overridable so the tests can drive a temp directory instead of the
    /// real Application Support folder.
    static var directory: URL = {
        FileManager.default.urls(for: .applicationSupportDirectory,
                                 in: .userDomainMask).first!
            .appendingPathComponent("ClipSync", isDirectory: true)
    }()

    static var fileURL: URL { directory.appendingPathComponent("debug.log") }
    static var rotatedURL: URL { directory.appendingPathComponent("debug.log.1") }
    /// Presence of this file turns file logging on for the next launch, and
    /// is what the Settings toggle writes. Same name Windows uses.
    static var markerURL: URL { directory.appendingPathComponent("debug-enabled") }

    private static let lock = NSLock()
    /// nil until first resolved, then cached — matching Windows' `_logEnabled`.
    private static var enabled: Bool?

    /// Force logging on for this run regardless of marker or environment.
    /// Called from the composition root when `--debug` is passed.
    static func enableForThisRun() {
        lock.lock(); defer { lock.unlock() }
        enabled = true
    }

    /// Is file logging on? Resolves the env var and marker file once.
    static var isEnabled: Bool {
        lock.lock(); defer { lock.unlock() }
        return resolveLocked()
    }

    /// Turn file logging on or off from the UI. Flips the in-memory flag so
    /// it takes effect immediately, and writes/removes the marker so it
    /// survives a relaunch — the two halves the Settings toggle needs.
    static func setEnabled(_ on: Bool) {
        // Say we are stopping while the file sink is still open. Flipping
        // first would end the log mid-sentence, which reads exactly like a
        // crash to whoever opens it later.
        if !on { write("Log: file logging off") }

        lock.lock()
        enabled = on
        lock.unlock()

        do {
            if on {
                try FileManager.default.createDirectory(at: directory,
                                                        withIntermediateDirectories: true)
                if !FileManager.default.fileExists(atPath: markerURL.path) {
                    try Data().write(to: markerURL)
                }
            } else if FileManager.default.fileExists(atPath: markerURL.path) {
                try FileManager.default.removeItem(at: markerURL)
            }
        } catch {
            // Both branches say what happened: a toggle that silently failed
            // to persist would come back on (or off) at the next launch with
            // nothing to explain why.
            write("Log: could not \(on ? "create" : "remove") the marker: \(error)")
            return
        }
        if on { write("Log: file logging on") }
    }

    /// One diagnostic line: always to stderr, and to the log file when
    /// enabled. Never pass clipboard content or key material.
    static func write(_ message: String) {
        NSLog("%@", message)

        lock.lock()
        let on = resolveLocked()
        lock.unlock()
        guard on else { return }

        lock.lock(); defer { lock.unlock() }
        do {
            try FileManager.default.createDirectory(at: directory,
                                                    withIntermediateDirectories: true)
            rotateIfNeededLocked()
            let line = "\(timestamp()) \(message)\n"
            guard let data = line.data(using: .utf8) else { return }
            if let handle = try? FileHandle(forWritingTo: fileURL) {
                defer { try? handle.close() }
                try handle.seekToEnd()
                try handle.write(contentsOf: data)
            } else {
                try data.write(to: fileURL)
            }
        } catch {
            // Logging must never take the app down, and there is nowhere
            // left to report to — stderr above already carried the message.
        }
    }

    /// printf-style overload, so the call sites that grew up around NSLog
    /// keep their `%@`/`%d` formatting instead of being rewritten into
    /// interpolation (and risking a transcription error in the rewrite).
    static func write(_ format: String, _ args: CVarArg...) {
        write(String(format: format, arguments: args))
    }

    // MARK: - Internals

    private static func resolveLocked() -> Bool {
        if let enabled { return enabled }
        let resolved = ProcessInfo.processInfo.environment["CLIPSYNC_DEBUG"] == "1"
            || FileManager.default.fileExists(atPath: markerURL.path)
        enabled = resolved
        return resolved
    }

    private static func rotateIfNeededLocked() {
        let fm = FileManager.default
        guard let attrs = try? fm.attributesOfItem(atPath: fileURL.path),
              let size = attrs[.size] as? NSNumber,
              size.intValue > maxBytes else { return }
        try? fm.removeItem(at: rotatedURL)
        try? fm.moveItem(at: fileURL, to: rotatedURL)
    }

    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss.SSS"     // matches Windows' HH:mm:ss.fff
        return f
    }()

    private static func timestamp() -> String { formatter.string(from: Date()) }

    /// Test seam: forget the cached gate so the next call re-reads the
    /// environment and marker file.
    static func resetForTesting(directory dir: URL) {
        lock.lock(); defer { lock.unlock() }
        directory = dir
        enabled = nil
    }
}
