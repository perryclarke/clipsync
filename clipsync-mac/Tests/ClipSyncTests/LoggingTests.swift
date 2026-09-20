import XCTest
@testable import ClipSync

/// The file sink's gate and rotation. Everything runs against a temp
/// directory via `Log.resetForTesting`, so the real
/// ~/Library/Application Support/ClipSync log is never touched.
///
/// Serialised: Log is process-global state (a cached flag and a directory),
/// so these must not interleave.
final class LoggingTests: XCTestCase {
    private var dir: URL!

    override func setUp() {
        super.setUp()
        dir = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("clipsync-log-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        Log.resetForTesting(directory: dir)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: dir)
        super.tearDown()
    }

    private var logExists: Bool {
        FileManager.default.fileExists(atPath: Log.fileURL.path)
    }

    private func logContents() -> String {
        (try? String(contentsOf: Log.fileURL, encoding: .utf8)) ?? ""
    }

    func testNothingIsWrittenUntilEnabled() {
        XCTAssertFalse(Log.isEnabled)
        Log.write("a line that should not reach the file")
        XCTAssertFalse(logExists, "the file sink must stay shut while logging is off")
    }

    func testEnablingWritesTheMarkerAndTheFile() {
        Log.setEnabled(true)
        XCTAssertTrue(Log.isEnabled)
        XCTAssertTrue(FileManager.default.fileExists(atPath: Log.markerURL.path),
                      "the marker is what survives a relaunch")

        Log.write("discovery: browse results: 2")
        XCTAssertTrue(logContents().contains("discovery: browse results: 2"))
    }

    func testDisablingRemovesTheMarkerAndStopsWriting() {
        Log.setEnabled(true)
        Log.write("before")
        Log.setEnabled(false)
        Log.write("after")

        XCTAssertFalse(FileManager.default.fileExists(atPath: Log.markerURL.path))
        let text = logContents()
        XCTAssertTrue(text.contains("before"))
        XCTAssertFalse(text.contains("after"), "no lines once the toggle is off")
    }

    /// The marker alone turns logging on at the next launch — the mechanism
    /// Windows uses, and what makes the toggle survive a restart.
    func testMarkerFileAloneEnablesOnAFreshLaunch() {
        try? Data().write(to: dir.appendingPathComponent("debug-enabled"))
        Log.resetForTesting(directory: dir)          // as if just launched
        XCTAssertTrue(Log.isEnabled)
    }

    func testEnableForThisRunOverridesAnAbsentMarker() {
        Log.enableForThisRun()
        XCTAssertTrue(Log.isEnabled)
        Log.write("enabled by --debug")
        XCTAssertTrue(logContents().contains("enabled by --debug"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: Log.markerURL.path),
                       "--debug is for this run only; it must not persist")
    }

    func testOversizeLogRotatesToTheSiblingAndStartsClean() {
        Log.setEnabled(true)
        // One byte over the threshold is enough; rotation is checked before
        // the next line is appended.
        let big = Data(repeating: 0x41, count: Log.maxBytes + 1)
        try? big.write(to: Log.fileURL)

        Log.write("the line after rotation")

        XCTAssertTrue(FileManager.default.fileExists(atPath: Log.rotatedURL.path),
                      "the old log moves aside to debug.log.1")
        let text = logContents()
        XCTAssertTrue(text.contains("the line after rotation"))
        XCTAssertFalse(text.contains("AAAA"), "the new log starts empty")
    }

    func testLinesCarryATimestampAndTheMessage() {
        Log.setEnabled(true)
        Log.write("PeerRegistry: sending to abcd1234")
        let line = logContents()
            .split(separator: "\n")
            .first { $0.contains("sending to") }
            .map(String.init) ?? ""
        // HH:mm:ss.SSS, matching the Windows format.
        XCTAssertNotNil(line.range(of: #"^\d{2}:\d{2}:\d{2}\.\d{3} "#, options: .regularExpression),
                        "unexpected line shape: \(line)")
        XCTAssertTrue(line.hasSuffix("PeerRegistry: sending to abcd1234"))
    }

    func testFormatArgumentsAreExpanded() {
        Log.setEnabled(true)
        Log.write("Broadcast: sending to %@ (%d formats)", "abcd1234", 3)
        XCTAssertTrue(logContents().contains("Broadcast: sending to abcd1234 (3 formats)"))
    }
}
