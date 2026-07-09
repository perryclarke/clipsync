import Foundation

/// Append-only log of clipboard items the app has sent or received. Used
/// by the test tools to verify that a fuzzed local copy actually crossed
/// the wire. Lines are one per event, intended to be diffable against the
/// peer's log.
///
/// Format: `<iso8601-utc> <SEND|RECV> <bytes> <sha8> "<hint>"`
enum TransferLog {
    private static let queue = DispatcherQueue()

    static let path: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("ClipSync", isDirectory: true)
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        return base.appendingPathComponent("transfers.log")
    }()

    static func send(_ item: ClipboardItem) { write("SEND", item) }
    static func recv(_ item: ClipboardItem) { write("RECV", item) }

    private static func write(_ direction: String, _ item: ClipboardItem) {
        let total = item.formats.reduce(0) { $0 + Int($1.size) }
        let sha8 = item.canonicalHash.prefix(4).map { String(format: "%02x", $0) }.joined()
        let hint = escape(item.hint ?? "")
        let line = "\(isoNow()) \(direction) \(total) \(sha8) \"\(hint)\"\n"
        queue.run {
            if let data = line.data(using: .utf8) {
                if let fh = try? FileHandle(forWritingTo: path) {
                    fh.seekToEndOfFile()
                    fh.write(data)
                    try? fh.close()
                } else {
                    try? data.write(to: path)
                }
            }
        }
    }

    private static let isoFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    private static func isoNow() -> String { isoFormatter.string(from: Date()) }

    private static func escape(_ s: String) -> String {
        var out = ""
        for ch in s {
            switch ch {
            case "\\": out += "\\\\"
            case "\"": out += "\\\""
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            default: out.append(ch)
            }
        }
        return out
    }
}

/// Tiny serial queue wrapper so log writes from multiple threads don't
/// interleave on a single line.
private final class DispatcherQueue {
    private let q = DispatchQueue(label: "clipsync.transferlog")
    func run(_ body: @escaping () -> Void) { q.async(execute: body) }
}
