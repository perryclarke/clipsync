import AppKit
import Foundation

/// Applies a remote ClipboardItem to the local NSPasteboard. Writing
/// automatically adds the item to macOS 15's Clipboard History.
///
/// A dedupe ring remembers the canonical hashes we just wrote so that
/// the watcher's next tick does not re-broadcast the item we just
/// received.
final class PasteboardWriter {
    private let pasteboard = NSPasteboard.general
    private var recentWrites: [(hash: Data, expiry: Date)] = []
    private let lock = NSLock()

    func apply(_ item: ClipboardItem) {
        let pbItem = NSPasteboardItem()
        for f in item.formats {
            guard case .inline(let data) = f.payload else { continue }
            let type = NSPasteboard.PasteboardType(mime_to_uti(f.mime))
            pbItem.setData(data, forType: type)
        }
        lock.lock()
        recentWrites.append((item.canonicalHash, Date().addingTimeInterval(5)))
        lock.unlock()
        pasteboard.clearContents()
        pasteboard.writeObjects([pbItem])
    }

    func consumeRecentWrite(matching hash: Data) -> Bool {
        lock.lock(); defer { lock.unlock() }
        let now = Date()
        recentWrites.removeAll { $0.expiry < now }
        if let idx = recentWrites.firstIndex(where: { $0.hash == hash }) {
            recentWrites.remove(at: idx)
            return true
        }
        return false
    }
}
