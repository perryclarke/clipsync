import XCTest
import AppKit
@testable import ClipSync

/// The menu-bar icon went missing in 0.8.1 because the clipboard glyph was
/// drawn in `NSColor.labelColor`: a status item is rasterised under
/// `NSAppearanceNameVibrantLight` even on a dark bar in Dark Mode, so that
/// resolved to black and the glyph was painted black on black. Nothing in
/// the app said so — it read as a failure to launch.
///
/// So what these tests pin is **contrast**: in each appearance the icon has
/// ink in it, and that ink is light against a dark bar and dark against a
/// light one. A badge that only differed by colour would also be a
/// regression, so the shapes are checked to differ too.
final class StatusIconTests: XCTestCase {
    private static let badges: [StatusIcon.Badge] = [.syncing, .paused, .attention]

    func testGlyphIsLightOnADarkBar() throws {
        let dark = try XCTUnwrap(NSAppearance(named: .darkAqua))
        for badge in Self.badges {
            let ink = try Self.glyphInk(StatusIcon.make(badge: badge, appearance: dark))
            XCTAssertGreaterThan(ink, 0.6,
                                 "\(badge) glyph must be light against a dark menu bar")
        }
    }

    func testGlyphIsDarkOnALightBar() throws {
        let light = try XCTUnwrap(NSAppearance(named: .aqua))
        for badge in Self.badges {
            let ink = try Self.glyphInk(StatusIcon.make(badge: badge, appearance: light))
            XCTAssertLessThan(ink, 0.4,
                              "\(badge) glyph must be dark against a light menu bar")
        }
    }

    /// The drawing handler runs lazily, in whatever appearance happens to be
    /// current when the image is rasterised — which is the trap that caused
    /// the bug. Rasterising the *same* image under the opposite appearance
    /// must not change what was baked into it.
    func testInkDoesNotFollowTheRasterisingContext() throws {
        let dark = try XCTUnwrap(NSAppearance(named: .darkAqua))
        let light = try XCTUnwrap(NSAppearance(named: .vibrantLight))
        var inkUnderLightContext: CGFloat = 0
        try light.performAsCurrentDrawingAppearance {
            inkUnderLightContext = (try? Self.glyphInk(
                StatusIcon.make(badge: .syncing, appearance: dark))) ?? 0
        }
        XCTAssertGreaterThan(inkUnderLightContext, 0.6,
                             "an icon built for a dark bar must stay light even when "
                             + "rasterised under a light drawing appearance")
    }

    /// Each badge is a different shape, so the states are still told apart
    /// by someone who cannot see the colour — and at the 18 pt the menu bar
    /// actually draws, where there is very little room to differ in.
    func testBadgesAreDistinguishableByShape() throws {
        let dark = try XCTUnwrap(NSAppearance(named: .darkAqua))
        let shapes = try Self.badges.map { try Self.symbolShape(
            StatusIcon.make(badge: $0, appearance: dark)) }
        XCTAssertEqual(Set(shapes).count, shapes.count,
                       "badges must differ in shape, not only in colour:\n"
                       + shapes.joined(separator: "\n---\n"))
    }

    /// The disc is the one part that is coloured in both appearances, so it
    /// is what keeps the icon findable if the bar and the app ever disagree.
    func testBadgeDiscIsBlueInBothAppearances() throws {
        for name in [NSAppearance.Name.aqua, .darkAqua] {
            let appearance = try XCTUnwrap(NSAppearance(named: name))
            let image = StatusIcon.make(badge: .syncing, appearance: appearance)
            let rep = try Self.bitmap(image)
            // Bottom-trailing corner, where the disc sits.
            let colour = try XCTUnwrap(rep.colorAt(x: rep.pixelsWide - 3,
                                                   y: rep.pixelsHigh - 6))
            XCTAssertGreaterThan(colour.blueComponent, 0.8,
                                 "disc should be blue under \(name.rawValue)")
            XCTAssertLessThan(colour.redComponent, 0.4,
                              "disc should be blue under \(name.rawValue)")
        }
    }

    // MARK: Pixel helpers

    private static func bitmap(_ image: NSImage) throws -> NSBitmapImageRep {
        let tiff = try XCTUnwrap(image.tiffRepresentation)
        let raw = try XCTUnwrap(NSBitmapImageRep(data: tiff))
        return try XCTUnwrap(raw.converting(to: .sRGB, renderingIntent: .default))
    }

    /// Mean brightness of the clipboard glyph: the left two-thirds of the
    /// icon, which the badge disc never reaches.
    private static func glyphInk(_ image: NSImage) throws -> CGFloat {
        let rep = try bitmap(image)
        var sum: CGFloat = 0
        var n: CGFloat = 0
        for y in 0..<rep.pixelsHigh {
            for x in 0..<(rep.pixelsWide * 2 / 3) {
                guard let c = rep.colorAt(x: x, y: y), c.alphaComponent > 0.5 else { continue }
                sum += (c.redComponent + c.greenComponent + c.blueComponent) / 3
                n += 1
            }
        }
        XCTAssertGreaterThan(n, 30, "almost nothing was drawn")
        return n == 0 ? 0 : sum / n
    }

    /// The white symbol inside the badge disc, as a picture. A pixel count is
    /// not enough — `wifi` and `questionmark` light the same number of pixels
    /// at this size — so compare where they land, and print the shapes on
    /// failure so the diff is readable.
    ///
    /// The red channel separates mark from disc: the disc is blue, so its red
    /// is near zero, while the mark is white. Testing for *pure* white would
    /// miss the thin strokes, which antialias to a pale blue against it.
    private static func symbolShape(_ image: NSImage) throws -> String {
        let rep = try bitmap(image)
        let disc = rep.pixelsWide * 11 / 18          // the disc's share of the icon
        var rows: [String] = []
        for y in (rep.pixelsHigh - disc)..<rep.pixelsHigh {
            var row = ""
            for x in (rep.pixelsWide - disc)..<rep.pixelsWide {
                let c = rep.colorAt(x: x, y: y)
                let mark = (c?.alphaComponent ?? 0) > 0.5 && (c?.redComponent ?? 0) > 0.5
                row += mark ? "#" : "."
            }
            rows.append(row)
        }
        return rows.joined(separator: "\n")
    }
}
