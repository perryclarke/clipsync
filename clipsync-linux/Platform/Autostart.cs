using System;
using System.IO;
using System.Linq;

namespace ClipSync.Platform;

/// The "Start ClipSync when you sign in" switch, backed by XDG autostart.
///
/// Two files are in play. The .deb installs a system-wide entry in
/// /etc/xdg/autostart, so a packaged install starts at login out of the
/// box; a per-user file of the same name in ~/.config/autostart overrides
/// it, and `Hidden=true` there is the XDG way to say "not for this user".
/// So the switch never touches the system file: turning off writes a
/// Hidden override, turning on removes it (or, with no system entry — a
/// dev build — writes a real user entry pointing at this binary).
internal sealed class Autostart
{
    private const string FileName = "clipsync.desktop";

    private readonly string _userFile;
    private readonly string _systemFile;

    public Autostart(string? userDir = null, string? systemFile = null)
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome))
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        _userFile = Path.Combine(userDir ?? Path.Combine(configHome, "autostart"), FileName);
        _systemFile = systemFile ?? Path.Combine("/etc/xdg/autostart", FileName);
    }

    public bool IsEnabled()
    {
        if (File.Exists(_userFile))
        {
            try
            {
                return !File.ReadLines(_userFile).Any(
                    l => l.Replace(" ", "").Equals("Hidden=true",
                                                   StringComparison.OrdinalIgnoreCase));
            }
            catch (IOException) { return false; }
        }
        return File.Exists(_systemFile);
    }

    public void SetEnabled(bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_userFile)!);

        if (enabled)
        {
            if (File.Exists(_systemFile))
            {
                // The system entry already starts us; just drop the override.
                File.Delete(_userFile);
                return;
            }
            File.WriteAllText(_userFile, $"""
                [Desktop Entry]
                Type=Application
                Name=ClipSync
                Comment=LAN-only encrypted clipboard sync
                Exec={Environment.ProcessPath}
                Terminal=false
                Categories=Utility;
                NoDisplay=true
                X-GNOME-Autostart-enabled=true
                """ + Environment.NewLine);
        }
        else if (File.Exists(_systemFile))
        {
            // Only the marker matters to the spec; Name is a courtesy for
            // anyone reading the file.
            File.WriteAllText(_userFile, """
                [Desktop Entry]
                Type=Application
                Name=ClipSync
                Hidden=true
                """ + Environment.NewLine);
        }
        else
        {
            File.Delete(_userFile);
        }
    }
}
