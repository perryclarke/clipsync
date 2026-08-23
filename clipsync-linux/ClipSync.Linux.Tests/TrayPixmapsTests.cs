using ClipSync.Ui;
using Xunit;

namespace ClipSync.Linux.Tests;

/// The RGBA→ARGB32 repack behind the SNI IconPixmap property. The SVG
/// rendering itself needs librsvg and is exercised by running the daemon;
/// what can silently regress is the byte shuffle.
public class TrayPixmapsTests
{
    [Fact]
    public void RgbaBecomesNetworkOrderArgb()
    {
        byte[] rgba = [10, 20, 30, 40];
        Assert.Equal(new byte[] { 40, 10, 20, 30 },
                     TrayPixmaps.ToArgbNetworkOrder(rgba, 1, 1, 4, 4));
    }

    [Fact]
    public void RgbWithoutAlphaBecomesOpaque()
    {
        byte[] rgb = [10, 20, 30];
        Assert.Equal(new byte[] { 255, 10, 20, 30 },
                     TrayPixmaps.ToArgbNetworkOrder(rgb, 1, 1, 3, 3));
    }

    [Fact]
    public void RowstridePaddingIsDropped()
    {
        // Two 1-pixel RGBA rows padded to an 8-byte stride; the pad bytes
        // (99) must not leak into the output.
        byte[] pixels =
        [
            1, 2, 3, 4, 99, 99, 99, 99,
            5, 6, 7, 8,
        ];
        Assert.Equal(new byte[] { 4, 1, 2, 3, 8, 5, 6, 7 },
                     TrayPixmaps.ToArgbNetworkOrder(pixels, 1, 2, 8, 4));
    }
}
