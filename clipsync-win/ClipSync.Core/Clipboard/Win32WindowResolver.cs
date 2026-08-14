using System;
using System.Runtime.InteropServices;
using System.Text;
using ClipSync.Settings;

namespace ClipSync.Clipboard;

/// Resolves a window handle to the application that owns it.
///
/// The awkward part is Store apps: their foreground window belongs to
/// ApplicationFrameHost.exe, not to the app. The real app lives in a
/// child window of class Windows.UI.Core.CoreWindow, so we hop to that
/// window's process before asking who it is. Without this, excluding any
/// Store app would silently do nothing.
public sealed class Win32WindowResolver : IWindowResolver
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;
    private const string FrameHost = "applicationframehost.exe";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    IntPtr IWindowResolver.GetForegroundWindow() => GetForegroundWindow();

    public AppIdentity? Resolve(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return null;
            if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0) return null;

            var exePath = ExePathOf(pid);
            if (exePath is null) return null;

            // Store app hosted in the frame host: hop to the real process.
            // Once we know the window belongs to the frame host, this hop is
            // the only route to a correct identity, so its failure must be
            // terminal -- falling through would report the frame host itself
            // as a confidently-wrong answer instead of failing open.
            if (string.Equals(System.IO.Path.GetFileName(exePath), FrameHost, StringComparison.OrdinalIgnoreCase))
            {
                if (FindCoreWindowPid(hwnd, pid) is not { } innerPid) return null;
                if (ExePathOf(innerPid) is not { } innerPath) return null;
                pid = innerPid;
                exePath = innerPath;
            }

            var family = PackageFamilyOf(pid, out var packageDetermined);
            if (family is not null)
                return new AppIdentity(AppKind.Package, family, family);

            // PackageFamilyOf collapses "definitely not packaged" and
            // "could not determine" unless we ask it not to. Only the former
            // justifies reporting an Exe identity; the latter must fail open
            // too, or a package the user excluded resolves to a non-matching
            // Exe identity instead of null.
            if (!packageDetermined) return null;

            // Bare file name, deliberately: Resolve runs on the UI thread's
            // message pump (WINEVENT_OUTOFCONTEXT) on every focus change, and
            // FileVersionInfo.GetVersionInfo is an uncached synchronous file
            // read that a network path or a stalled filesystem filter can
            // block on. Matching uses Key only, so nothing depends on this
            // name being the app's friendly one.
            //
            // It is not purely diagnostic, though: an app added through
            // "Exclude the app I switch to" is stored as the identity this
            // method returns, so the settings list shows this name rather
            // than the Start Menu one. That is why a captured app can appear
            // as "acrodist" where the picker would have said "Adobe Acrobat
            // Distiller". Reading the friendly name here is the wrong trade;
            // resolving it when the exclusion is stored would be the fix.
            return new AppIdentity(AppKind.Exe, exePath,
                                   System.IO.Path.GetFileNameWithoutExtension(exePath), exePath);
        }
        catch (Exception ex)
        {
            Security.Log.Write($"Win32WindowResolver: resolve failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static string? ExePathOf(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        // PROCESS_QUERY_LIMITED_INFORMATION exists precisely so a medium-IL
        // process can query a high-IL one; a null handle here is more likely
        // a protected/system process, or a race where the process already
        // exited, than elevation as such. The error code is logged so the two
        // are distinguishable: ERROR_ACCESS_DENIED (5) means the access check
        // refused us, ERROR_INVALID_PARAMETER (87) means the PID was already
        // gone by the time we asked.
        if (h == IntPtr.Zero)
        {
            Security.Log.Write($"Win32WindowResolver: OpenProcess({pid}) failed, error {Marshal.GetLastWin32Error()}");
            return null;
        }
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString();

            Security.Log.Write($"Win32WindowResolver: QueryFullProcessImageName({pid}) failed, " +
                               $"error {Marshal.GetLastWin32Error()}");
            return null;
        }
        finally { CloseHandle(h); }
    }

    /// The package family name, or null if none. `determined` distinguishes
    /// "definitely not a packaged app" (APPMODEL_ERROR_NO_PACKAGE -- safe to
    /// fall through to an Exe identity) from "could not tell" (OpenProcess
    /// or the second GetPackageFamilyName call failed -- caller must fail
    /// open rather than guess).
    private static string? PackageFamilyOf(uint pid, out bool determined)
    {
        determined = false;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
        {
            Security.Log.Write($"Win32WindowResolver: OpenProcess({pid}) for package lookup failed, " +
                               $"error {Marshal.GetLastWin32Error()}");
            return null;
        }
        try
        {
            uint len = 0;
            var rc = GetPackageFamilyName(h, ref len, null);
            if (rc == APPMODEL_ERROR_NO_PACKAGE)
            {
                determined = true;
                return null;
            }
            if (len == 0) return null;   // unexpected shape: indeterminate

            var sb = new StringBuilder((int)len);
            rc = GetPackageFamilyName(h, ref len, sb);
            if (rc != 0) return null;    // second call failed: indeterminate

            determined = true;
            return sb.ToString();
        }
        finally { CloseHandle(h); }
    }

    /// The PID of the CoreWindow child, when it differs from the frame host.
    private static uint? FindCoreWindowPid(IntPtr parent, uint hostPid)
    {
        uint found = 0;
        EnumChildWindows(parent, (child, _) =>
        {
            var cls = new StringBuilder(256);
            if (GetClassName(child, cls, cls.Capacity) == 0) return true;
            if (!string.Equals(cls.ToString(), CoreWindowClass, StringComparison.Ordinal)) return true;

            if (GetWindowThreadProcessId(child, out var childPid) != 0 && childPid != hostPid)
            {
                found = childPid;
                return false;   // stop enumerating
            }
            return true;
        }, IntPtr.Zero);

        return found == 0 ? null : found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    // SetLastError on these two so a failure is attributable: "resolve
    // failed" alone cannot tell an access-denied from a teardown race, and
    // that distinction is the whole answer to whether an elevated foreground
    // app can be excluded.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? name);
}
