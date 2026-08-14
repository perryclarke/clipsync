import AppKit

/// Composes the menu-bar icon at runtime: the clipboard glyph with a
/// badge over it — blue wifi when syncing, an orange pause when paused.
/// The macOS analogue of the Windows tray badge, composed the same way so
/// it cannot drift out of step with the real icon.
///
/// Built as an NSImage rather than a SwiftUI ZStack because MenuBarExtra
/// renders label views as template images, which strips the badge colour.
/// The drawing handler runs at draw time, so `labelColor` resolves
/// against the menu bar's current light/dark appearance.
enum StatusIcon {
    static func make(paused: Bool) -> NSImage {
        let size = NSSize(width: 18, height: 18)
        let image = NSImage(size: size, flipped: false) { rect in
            draw("list.clipboard.fill", pointSize: 14, weight: .regular,
                 color: .labelColor, in: rect, dy: 0)
            if paused {
                draw("pause.fill", pointSize: 8, weight: .heavy,
                     color: pauseOrange, in: rect, dy: -1.5)
            } else {
                draw("wifi", pointSize: 8, weight: .heavy,
                     color: wifiBlue, in: rect, dy: -1.5)
            }
            return true
        }
        image.isTemplate = false
        return image
    }

    /// The badge sits on the clipboard glyph, which is labelColor — near
    /// white in a dark menu bar, near black in a light one. A single
    /// orange can't contrast with both, so resolve per appearance: darker
    /// against the light glyph, brighter against the dark one.
    private static let pauseOrange = NSColor(name: nil) { appearance in
        let dark = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        return dark
            ? NSColor(red: 0.72, green: 0.34, blue: 0.00, alpha: 1)   // dark bar, light glyph
            : NSColor(red: 1.00, green: 0.62, blue: 0.04, alpha: 1)   // bright bar, dark glyph
    }

    /// Same per-appearance treatment as the pause bars: in a light menu
    /// bar the clipboard glyph is near black, where systemBlue is too
    /// dark to read — use a lighter blue there.
    private static let wifiBlue = NSColor(name: nil) { appearance in
        appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
            ? .systemBlue                                             // blue bar, light glyph
            : NSColor(red: 0.42, green: 0.72, blue: 1.00, alpha: 1)   // light bar, dark glyph
    }

    private static func draw(_ symbolName: String, pointSize: CGFloat,
                             weight: NSFont.Weight, color: NSColor,
                             in rect: NSRect, dy: CGFloat) {
        let config = NSImage.SymbolConfiguration(pointSize: pointSize, weight: weight)
            .applying(.init(paletteColors: [color]))
        guard let symbol = NSImage(systemSymbolName: symbolName,
                                   accessibilityDescription: nil)?
            .withSymbolConfiguration(config) else { return }
        let s = symbol.size
        let origin = NSPoint(x: rect.midX - s.width / 2,
                             y: rect.midY - s.height / 2 + dy)
        symbol.draw(in: NSRect(origin: origin, size: s))
    }
}
