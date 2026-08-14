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
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// Width of the invisible resize border on each side, in AppWindow units.
    ///
    /// Measured from the drawn window where that is possible, since only the
    /// measurement gets the top edge right: the border is not symmetric, and
    /// the couple of pixels above the caption are not something the system
    /// metrics below describe. Falls back to those metrics for a window that
    /// has not been drawn yet, where DWM has no frame to report and answers
    /// with a rectangle that is not the window's.
    ///
    /// All zeroes on failure, which leaves callers doing exactly what they
    /// did before this existed.
    internal static (int Left, int Top, int Right, int Bottom) ResizeBorder(IntPtr hwnd)
    {
        try
        {
            return Measured(hwnd) ?? FromSystemMetrics(hwnd);
        }
        catch
        {
            return (0, 0, 0, 0);
        }
    }

    /// The real border of a window that is already on screen, or null.
    private static (int Left, int Top, int Right, int Bottom)? Measured(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame,
                                  Marshal.SizeOf<RECT>()) != 0)
            return null;
        if (!GetWindowRect(hwnd, out var window))
            return null;

        var insets = (Left: frame.Left - window.Left,
                      Top: frame.Top - window.Top,
                      Right: window.Right - frame.Right,
                      Bottom: window.Bottom - frame.Bottom);

        // A real border is a handful of pixels and never negative. Anything
        // else is DWM describing a window it has not composed.
        if (insets.Left < 0 || insets.Top < 0 || insets.Right < 0 || insets.Bottom < 0 ||
            insets.Left > 64 || insets.Top > 64 || insets.Right > 64 || insets.Bottom > 64)
            return null;

        return insets;
    }

    /// The system's idea of the border, available before the window exists on
    /// screen. Close, but it has nothing to say about the top edge.
    private static (int Left, int Top, int Right, int Bottom) FromSystemMetrics(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;

        // The padded border is counted on top of the sizing frame; the two
        // together are what the frame actually extends by.
        var padded = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
        var horizontal = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + padded;
        var vertical = GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + padded;

        if (horizontal < 0 || vertical < 0 || horizontal > 64 || vertical > 64)
            return (0, 0, 0, 0);

        return (horizontal, 0, horizontal, vertical);
    }
}
