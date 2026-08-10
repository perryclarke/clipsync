using System;
using ClipSync.Settings;

namespace ClipSync.Clipboard;

/// Isolates the Win32 calls so ForegroundTracker's logic is testable and
/// so a resolution failure has one obvious place to live.
public interface IWindowResolver
{
    IntPtr GetForegroundWindow();

    /// The app owning `hwnd`, or null if it cannot be determined
    /// (elevated process, race with window teardown, etc.).
    AppIdentity? Resolve(IntPtr hwnd);
}
