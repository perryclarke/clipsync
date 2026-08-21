using System;
using Microsoft.Win32;

namespace ClipSync;

/// The "start when you sign in" switch, backed by the same
/// HKCU\...\CurrentVersion\Run value the MSI writes at install time.
/// One value, two writers: the installer seeds it on, and this class is
/// how the user changes their mind afterwards. Note the value is the
/// KeyPath of the installer's autostart component, so a repair or major
/// upgrade re-creates it; a user who switched it off gets it back on the
/// next update, which is the price of the installer owning the default.
public static class StartupLauncher
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipSync";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            Security.Identity.Log($"StartupLauncher: read failed: {ex.GetType().Name}");
            return false;
        }
    }

    public static void SetEnabled(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
            {
                // The running executable, not the value the installer wrote:
                // a dev build toggling this on should start the dev build.
                var exe = Environment.ProcessPath
                          ?? throw new InvalidOperationException("no process path");
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            Security.Identity.Log($"StartupLauncher: start-at-sign-in {(on ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            Security.Identity.Log($"StartupLauncher: write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
