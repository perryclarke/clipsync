import AppKit
import Foundation
import Crypto

/// Polls NSPasteboard.changeCount (the only reliable API) and emits a
/// ClipboardItem whenever the local user copies something.
final class PasteboardWatcher {
    var onLocalCopy: ((ClipboardItem) -> Void)?

    private let pasteboard = NSPasteboard.general
    private weak var writer: PasteboardWriter?
    private let identity: Identity
    private var lastChangeCount: Int = 0
    private var timer: Timer?
    private var nextSeq: UInt64 = 1

    init(identity: Identity, writer: PasteboardWriter) {
        self.identity = identity
        self.writer = writer
        self.lastChangeCount = pasteboard.changeCount
    }

    func start() {
        timer = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { [weak self] _ in
            self?.tick()
        }
    }

    func stop() { timer?.invalidate(); timer = nil }

    private func tick() {
        let current = pasteboard.changeCount
        guard current != lastChangeCount else { return }
        lastChangeCount = current

        guard let item = snapshot() else { return }

        // Loop suppression: if the writer just applied a remote item
        // with the same canonical hash, don't rebroadcast it.
        if writer?.consumeRecentWrite(matching: item.canonicalHash) == true { return }

        onLocalCopy?(item)
    }

    private func snapshot() -> ClipboardItem? {
        guard let items = pasteboard.pasteboardItems, !items.isEmpty else { return nil }
        var formats: [ClipFormat] = []

        for pbItem in items {
            for type in pbItem.types {
                guard let data = pbItem.data(forType: type) else { continue }
                let mime = uti_to_mime(type.rawValue)
                formats.append(ClipFormat(mime: mime, size: UInt64(data.count), payload: .inline(data)))
            }
        }
        guard !formats.isEmpty else { return nil }

        let item = ClipboardItem(
            seq: nextSeq,
            originDid: identity.did,
            tsMs: UInt64(Date().timeIntervalSince1970 * 1000),
            formats: formats,
            hint: firstTextHint(formats)
        )
        nextSeq &+= 1
        return item
    }

    private func firstTextHint(_ formats: [ClipFormat]) -> String? {
        for f in formats where f.mime.hasPrefix("text/") {
            if case .inline(let d) = f.payload, let s = String(data: d, encoding: .utf8) {
                return String(s.prefix(80))
            }
        }
        return nil
    }
}

/// Minimal UTI → MIME mapping. Covers the common clipboard types; unknown
/// UTIs are passed through as `application/x-uti;<name>` so the receiver
/// can round-trip them if it's the same OS.
func uti_to_mime(_ uti: String) -> String {
    switch uti {
    case "public.utf8-plain-text", "public.plain-text": return "text/plain;charset=utf-8"
    case "public.rtf":      return "text/rtf"
    case "public.html":     return "text/html"
    case "public.png":      return "image/png"
    case "public.jpeg":     return "image/jpeg"
    case "public.tiff":     return "image/tiff"
    case "public.file-url": return "application/x-file-url"
    default:                return "application/x-uti;" + uti
    }
}

func mime_to_uti(_ mime: String) -> String {
    switch mime {
    case "text/plain;charset=utf-8", "text/plain": return "public.utf8-plain-text"
    case "text/rtf":    return "public.rtf"
    case "text/html":   return "public.html"
    case "image/png":   return "public.png"
    case "image/jpeg":  return "public.jpeg"
    case "image/tiff":  return "public.tiff"
    case "application/x-file-url": return "public.file-url"
    default:
        if mime.hasPrefix("application/x-uti;") {
            return String(mime.dropFirst("application/x-uti;".count))
        }
        return "public.data"
    }
}
