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
    private let foreground: ForegroundRing
    private let settings: AppSettings
    private var lastChangeCount: Int = 0
    private var lastTick = Date()
    private var timer: Timer?
    private var nextSeq: UInt64 = 1

    init(identity: Identity, writer: PasteboardWriter,
         foreground: ForegroundRing, settings: AppSettings) {
        self.identity = identity
        self.writer = writer
        self.foreground = foreground
        self.settings = settings
        self.lastChangeCount = pasteboard.changeCount
    }

    func start() {
        // macOS 26 gates programmatic pasteboard reads per app. Log the
        // stance once at startup: if reads are denied, every copy would
        // otherwise vanish with no line saying why.
        if #available(macOS 15.4, *) {
            NSLog("PasteboardWatcher: pasteboard access behavior = %d",
                  pasteboard.accessBehavior.rawValue)
        }
        lastTick = Date()
        // .common, not the default mode: a default-mode timer stalls while
        // a menu or popover is tracking, so a copy made from a context menu
        // (or while our own popover is open) would sit unprocessed — and
        // rapid copies would coalesce into one once the menu closed.
        let t = Timer(timeInterval: 0.2, repeats: true) { [weak self] _ in
            self?.tick()
        }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    func stop() { timer?.invalidate(); timer = nil }

    private func tick() {
        // The copy is only known to have happened somewhere in the window
        // since the previous tick, so that window — not "now" — is what
        // the suppression decision is asked about.
        let now = Date()
        let windowStart = lastTick
        lastTick = now

        let current = pasteboard.changeCount
        guard current != lastChangeCount else { return }
        lastChangeCount = current

        guard let item = snapshot() else {
            // A change with nothing readable is worth one line: without it,
            // a denied pasteboard read is indistinguishable from no copy
            // ever happening.
            NSLog("PasteboardWatcher: change #%d had no readable formats", current)
            return
        }

        // Loop suppression stays first: if the exclusion check
        // short-circuited it, the recent-write marker would survive into
        // the next copy and cause a spurious echo.
        if writer?.consumeRecentWrite(matching: item.canonicalHash) == true { return }

        // Suppress transmission only — the item is already in the local
        // clipboard and clipboard history, and deliberately stays there.
        // Both branches log: "no suppression line" alone must never be
        // the evidence that a copy went out, because it is
        // indistinguishable from the copy never reaching this decision.
        let decision = SuppressionPolicy.decide(ring: foreground, settings: settings,
                                                windowStart: windowStart, windowEnd: now)
        if decision.suppress {
            NSLog("PasteboardWatcher: suppressed item from %@ (%d formats)",
                  decision.source?.displayName ?? "?", item.formats.count)
            return
        }
        NSLog("PasteboardWatcher: sending item (%d formats)", item.formats.count)

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
