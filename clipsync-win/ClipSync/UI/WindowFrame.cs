using System;
using System.Runtime.InteropServices;

namespace ClipSync.UI;

/// The gap between where Windows says a window is and where it looks like it
/// is.
///
/// A resizable window's rect extends past its visible frame by the width of
/// the invisible resize border, on the sides and bottom but not above the
/// caption. `AppWindow.Move` positions by that rect, so a window lined up
/// against something without such a border -- a context-menu presenter, say
/// -- sits a few pixels high, and nothing about the numbers involved says so.
///
/// This asks the system for the border width rather than measuring the drawn
/// window with DWM. DWM answers in raw pixels while the rest of this app's
/// geometry is in AppWindow's units, and the two are not the same on a scaled
/// display; mixing them threw the settings window onto a different monitor.
/// `GetSystemMetricsForDpi` answers in the units implied by the DPI it is
/// handed, so passing the window's own DPI keeps every number here in the one
/// space. It also answers before the window has ever been shown, which DWM
/// does not: DWM has no frame to report until the window has been composed,
/// and returns something unusable if asked sooner.
internal static class WindowFrame
{
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// Width of the invisible resize border on each side, in AppWindow units.
    ///
    /// All zeroes for a window that cannot be resized, which has no such
    /// border, and on any failure -- which leaves callers doing exactly what
    /// they did before this existed.
    internal static (int Left, int Top, int Right, int Bottom) ResizeBorder(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;

            // The padded border is counted on top of the sizing frame; the
            // two together are what the frame actually extends by.
            var padded = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
            var horizontal = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + padded;
            var vertical = GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + padded;

            if (horizontal < 0 || vertical < 0 || horizontal > 64 || vertical > 64)
                return (0, 0, 0, 0);

            // No invisible border above the caption on Windows 11.
            return (horizontal, 0, horizontal, vertical);
        }
        catch
        {
            return (0, 0, 0, 0);
        }
    }
}
