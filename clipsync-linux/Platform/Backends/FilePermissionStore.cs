using System;
using System.IO;
using ClipSync.Platform;

namespace ClipSync.Platform.Backends;

/// Secrets as owner-only files under the app's data directory.
///
/// This is weaker than DPAPI or the Keychain and deliberately so: it is the
/// backend that works everywhere, including before any keyring is unlocked,
/// and the threat model (§ PROTOCOL.md — a LAN-only personal tool) is about
/// other machines on the subnet, not other users on this one. A libsecret
/// backend can be added later behind ISecretStore without touching Identity.
///
/// Files are written 0600 *before* any bytes reach them: creating a file
/// with default permissions and chmod-ing afterwards leaves a window where
/// the key is world-readable.
public sealed class FilePermissionStore : ISecretStore
{
    public string Name => "FilePermissionStore";

    private readonly string _dir;

    public FilePermissionStore(string dir)
    {
        _dir = dir;
    }

    /// The shared per-user data directory. The linked AppSettings resolves
    /// LocalApplicationData + "ClipSync" the same way, so settings, trust
    /// and keys all land together (~/.local/share/ClipSync on XDG systems).
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipSync");

    public bool IsAvailable()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            return true;
        }
        catch { return false; }
    }

    private string PathFor(string key) => Path.Combine(_dir, key);

    public byte[]? Load(string key)
    {
        var path = PathFor(key);
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    public void Save(string key, byte[] value)
    {
        Directory.CreateDirectory(_dir);
        var path = PathFor(key);
        var tmp = path + ".tmp";

        // Create with 0600 up front, then write — never widen-then-narrow.
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using (var fs = new FileStream(tmp, options))
        {
            fs.Write(value, 0, value.Length);
            fs.Flush(true);
        }

        // Atomic replace, so a crash mid-write cannot leave a truncated key.
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string key)
    {
        try { File.Delete(PathFor(key)); } catch { }
    }
}
