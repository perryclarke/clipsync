import AppKit

/// Composes the menu-bar icon at runtime: the clipboard glyph with a badge
/// over it saying what ClipSync is doing — syncing, paused, or waiting for
/// the user to trust a device.
///
/// **Never resolve a dynamic colour inside the drawing handler.** A status
/// item is rasterised in whatever appearance the menu bar happens to use,
/// and that is not the app's: logging `NSAppearance.currentDrawing()` from
/// in here shows the handler invoked under `NSAppearanceNameVibrantLight`
/// *even in Dark Mode*. `NSColor.labelColor` therefore resolved to black at
/// 70% alpha, the glyph was painted black on a black bar, and the icon was
/// invisible — which read as ClipSync failing to launch at all.
///
/// So the ink is chosen here, explicitly, from `NSApp.effectiveAppearance`,
/// which does report the real Light/Dark setting from inside the handler.
/// The badge disc is blue in both, which is why it can carry a fixed white
/// symbol.
///
/// The remaining exposure is the reason Apple wants status items to be
/// template images: when the system is in Light Mode but the menu bar is
/// dark anyway — a dark desktop picture with wallpaper tinting on — the bar
/// no longer matches `effectiveAppearance`, and the clipboard glyph goes
/// dark-on-dark again. The blue disc still shows, so the icon is findable
/// rather than gone; but if that combination comes up, the fix is a template
/// image (`isTemplate = true`, everything drawn in black, AppKit tinting it
/// to suit the bar) at the cost of the colour.
///
/// Built as an NSImage rather than a SwiftUI ZStack because MenuBarExtra
/// renders label views as template images, which would strip the colour.
enum StatusIcon {
    /// Which badge rides on the clipboard glyph.
    enum Badge {
        /// Running normally.
        case syncing
        /// Sync is paused, globally.
        case paused
        /// A device has tried to connect and is waiting to be trusted — the
        /// user has to answer. A question mark rather than an exclamation
        /// mark on purpose: an unknown device asking to pair is a question,
        /// not a fault, and `!` reads as something having gone wrong.
        case attention

        var symbolName: String {
            switch self {
            case .syncing: return "wifi"
            case .paused: return "pause.fill"
            case .attention: return "questionmark"
            }
        }

        /// Clearance between the mark and the disc's edge.
        ///
        /// `wifi` gets less of it, because it is the one mark that is both
        /// stroked and wide: `fitted` scales a wide symbol to the box's
        /// *width*, so it ends up shorter than the filled marks and its
        /// strokes thinner with it. Matching their optical weight means
        /// giving it more of the disc. `.black` is already the heaviest
        /// weight SF Symbols offers, so size is the only lever left.
        var inset: CGFloat {
            switch self {
            case .syncing: return 1.6
            case .paused, .attention: return 2.25
            }
        }
    }

    /// Diameter of the badge disc in the bottom-trailing corner.
    private static let discSize: CGFloat = 11
    /// Cleared around the disc so it reads as sitting on top of the glyph
    /// rather than being part of it.
    private static let discMargin: CGFloat = 1

    /// macOS systemBlue's dark-appearance value — the same blue the Linux
    /// tray SVGs use, so the three platforms' icons match.
    private static let discBlue = NSColor(red: 0.04, green: 0.52, blue: 1.0, alpha: 1)

    /// - Parameter appearance: the appearance to pick ink against. Defaults
    ///   to the app's, which reports the real Light/Dark setting even when
    ///   the drawing context does not; injectable so the tests can check
    ///   both without changing the system setting.
    static func make(badge: Badge, appearance: NSAppearance? = nil) -> NSImage {
        let resolved = appearance ?? NSApp?.effectiveAppearance
        let darkBar = resolved?.bestMatch(from: [.aqua, .darkAqua]) != .aqua
        let size = NSSize(width: 18, height: 18)
        let image = NSImage(size: size, flipped: false) { rect in
            let ink: NSColor = darkBar ? .white : NSColor(white: 0.15, alpha: 1)
            if let glyph = symbol("list.clipboard.fill", pointSize: 14,
                                  weight: .regular, color: ink) {
                glyph.draw(in: centred(glyph.size, in: rect, dy: 0))
            }

            let disc = NSRect(x: rect.maxX - discSize, y: rect.minY,
                              width: discSize, height: discSize)
            // Clear a well a little larger than the disc first: against the
            // filled clipboard the blue would otherwise sit flush with the
            // glyph and the two shapes would run together.
            knockout(disc.insetBy(dx: -discMargin, dy: -discMargin))
            discBlue.setFill()
            NSBezierPath(ovalIn: disc).fill()

            // White on blue reads against a light and a dark bar alike,
            // which is the point of giving the disc a colour of its own.
            if let mark = symbol(badge.symbolName, pointSize: 7,
                                 weight: .black, color: .white) {
                mark.draw(in: fitted(mark.size,
                                     in: disc.insetBy(dx: badge.inset, dy: badge.inset)))
            }
            return true
        }
        image.isTemplate = false
        return image
    }

    private static func symbol(_ name: String, pointSize: CGFloat,
                               weight: NSFont.Weight, color: NSColor) -> NSImage? {
        let config = NSImage.SymbolConfiguration(pointSize: pointSize, weight: weight)
            .applying(.init(paletteColors: [color]))
        return NSImage(systemSymbolName: name, accessibilityDescription: nil)?
            .withSymbolConfiguration(config)
    }

    /// Centre a symbol in the icon, nudged vertically by `dy`.
    private static func centred(_ size: NSSize, in rect: NSRect, dy: CGFloat) -> NSRect {
        NSRect(origin: NSPoint(x: rect.midX - size.width / 2,
                               y: rect.midY - size.height / 2 + dy),
               size: size)
    }

    /// Scale a symbol down to fit `box` without distorting it. The badge
    /// symbols have very different aspect ratios — `wifi` is wide, a
    /// question mark is narrow — so each has to be fitted, not stretched.
    private static func fitted(_ size: NSSize, in box: NSRect) -> NSRect {
        let k = min(box.width / size.width, box.height / size.height)
        let s = NSSize(width: size.width * k, height: size.height * k)
        return NSRect(origin: NSPoint(x: box.midX - s.width / 2,
                                      y: box.midY - s.height / 2),
                      size: s)
    }

    /// Clear an oval out of what has been drawn so far, so the disc that
    /// lands in it is separated from the glyph behind it.
    private static func knockout(_ rect: NSRect) {
        guard let ctx = NSGraphicsContext.current else { return }
        let previous = ctx.compositingOperation
        ctx.compositingOperation = .destinationOut
        NSColor.black.setFill()
        NSBezierPath(ovalIn: rect).fill()
        ctx.compositingOperation = previous
    }
}
