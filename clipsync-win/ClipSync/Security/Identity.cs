using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;

namespace ClipSync.Security;

/// Device identity: a long-lived TLS certificate + an Ed25519 logical
/// key, both persisted to %LOCALAPPDATA%\ClipSync\ (key material
/// DPAPI-protected).
public sealed class Identity
{
    public static Identity Current { get; private set; } = null!;

    public byte[] Did { get; }              // SHA-256 of EC public key x963 bytes
    public string DidHex => Convert.ToHexString(Did).ToLowerInvariant();
    public X509Certificate2 TlsCertificate { get; }
    public Ed25519PrivateKeyParameters Ed25519Private { get; }

    private Identity(byte[] did, X509Certificate2 cert, Ed25519PrivateKeyParameters ed)
    {
        Did = did; TlsCertificate = cert; Ed25519Private = ed;
    }

    /// Compute the DID the same way the mac does: SHA-256 of the raw EC
    /// public key bytes (x963 uncompressed point, 65 bytes for P-256).
    /// On .NET, PublicKey.EncodedKeyValue.RawData for ECDSA contains the
    /// uncompressed point bytes — same as SecKeyCopyExternalRepresentation.
    public static byte[] ComputeDid(X509Certificate2 cert)
    {
        var raw = cert.PublicKey.EncodedKeyValue.RawData;
        return SHA256.HashData(raw);
    }

    public static Identity LoadOrCreate()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipSync");
        Directory.CreateDirectory(dir);
        var pfxPath = Path.Combine(dir, "tls.pfx.dpapi");
        var edPath = Path.Combine(dir, "ed25519.key.dpapi");

        X509Certificate2 cert;
        if (File.Exists(pfxPath))
        {
            var blob = ProtectedData.Unprotect(File.ReadAllBytes(pfxPath), null, DataProtectionScope.CurrentUser);
            cert = X509CertificateLoader.LoadPkcs12(blob, null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        else
        {
            cert = CreateSelfSignedCert();
            var blob = cert.Export(X509ContentType.Pfx);
            File.WriteAllBytes(pfxPath, ProtectedData.Protect(blob, null, DataProtectionScope.CurrentUser));
        }

        Ed25519PrivateKeyParameters ed;
        if (File.Exists(edPath))
        {
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(edPath), null, DataProtectionScope.CurrentUser);
            ed = new Ed25519PrivateKeyParameters(raw, 0);
        }
        else
        {
            var gen = new Ed25519KeyPairGenerator();
            gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var pair = gen.GenerateKeyPair();
            ed = (Ed25519PrivateKeyParameters)pair.Private;
            File.WriteAllBytes(edPath,
                ProtectedData.Protect(ed.GetEncoded(), null, DataProtectionScope.CurrentUser));
        }

        var did = ComputeDid(cert);
        var id = new Identity(did, cert, ed);
        Current = id;
        Log($"Identity loaded: did={id.DidHex}");
        return id;
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=clipsync", ecdsa, HashAlgorithmName.SHA256);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                        DateTimeOffset.UtcNow.AddYears(20));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    /// Diagnostic logging is opt-in (set CLIPSYNC_DEBUG=1 or create a
    /// `debug-enabled` file next to the log) and must never include
    /// clipboard content or key material — only network/protocol metadata.
    private static readonly object LogLock = new();
    private static bool? _logEnabled;

    /// Force diagnostic logging on regardless of env var / marker file.
    /// Called from Program.Main when `--debug` is passed on the command line.
    internal static void EnableLogging() => _logEnabled = true;

    internal static void Log(string msg)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipSync");
            _logEnabled ??= Environment.GetEnvironmentVariable("CLIPSYNC_DEBUG") == "1"
                            || File.Exists(Path.Combine(dir, "debug-enabled"));
            if (_logEnabled != true) return;
            lock (LogLock)
            {
                var path = Path.Combine(dir, "debug.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 5_000_000)
                    File.Move(path, Path.Combine(dir, "debug.log.1"), overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
            }
        }
        catch { }
    }
}
