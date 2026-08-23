using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClipSync.Platform;
using ClipSync.Platform.Backends;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace ClipSync.Security;

/// Device identity: a long-lived P-256 TLS certificate plus an Ed25519
/// logical key, persisted through an ISecretStore.
///
/// Deliberately mirrors the public surface of clipsync-win's Identity —
/// same namespace, same members — because PeerConnection and PeerRegistry
/// are linked from that tree and compile against this class unchanged. Only
/// the storage differs: DPAPI there, an owner-only file here.
public sealed class Identity
{
    public static Identity Current { get; private set; } = null!;

    /// The app's version as "major.minor.patch", from the assembly.
    public static readonly string AppVersion =
        typeof(Identity).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public byte[] Did { get; }
    public string DidHex => Convert.ToHexString(Did).ToLowerInvariant();
    public X509Certificate2 TlsCertificate { get; }
    public Ed25519PrivateKeyParameters Ed25519Private { get; }

    private const string TlsKey = "tls.pfx";
    private const string EdKey = "ed25519.key";

    private Identity(byte[] did, X509Certificate2 cert, Ed25519PrivateKeyParameters ed)
    {
        Did = did; TlsCertificate = cert; Ed25519Private = ed;
    }

    /// Compute the device id exactly as the mac and Windows clients do:
    /// SHA-256 over the raw EC public key bytes — the X9.63 uncompressed
    /// point, 65 bytes for P-256.
    ///
    /// NOTE: PROTOCOL.md §2 calls this the hash of the SPKI. It is not: the
    /// SPKI DER wraps this point in an AlgorithmIdentifier and a BIT STRING,
    /// and hashing that instead yields a different id and a peer that can
    /// never connect. Both shipping clients hash the point, so this does
    /// too. See HANDOFF.md, "did is not what PROTOCOL.md §2 says it is".
    /// IdentityTests pins this against an independent derivation.
    public static byte[] ComputeDid(X509Certificate2 cert)
    {
        var raw = cert.PublicKey.EncodedKeyValue.RawData;
        return SHA256.HashData(raw);
    }

    public static Identity LoadOrCreate() => LoadOrCreate(DefaultStore());

    /// Injectable store so tests can run against a temp directory rather
    /// than the real one in the user's home.
    public static Identity LoadOrCreate(ISecretStore store)
    {
        X509Certificate2 cert;
        var pfx = store.Load(TlsKey);
        if (pfx is not null)
        {
            cert = LoadCert(pfx);
        }
        else
        {
            cert = CreateSelfSignedCert();
            store.Save(TlsKey, cert.Export(X509ContentType.Pfx));
        }

        Ed25519PrivateKeyParameters ed;
        var edRaw = store.Load(EdKey);
        if (edRaw is not null)
        {
            ed = new Ed25519PrivateKeyParameters(edRaw, 0);
        }
        else
        {
            var gen = new Ed25519KeyPairGenerator();
            gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            ed = (Ed25519PrivateKeyParameters)gen.GenerateKeyPair().Private;
            store.Save(EdKey, ed.GetEncoded());
        }

        var id = new Identity(ComputeDid(cert), cert, ed);
        Current = id;
        Log($"Identity loaded: did={id.DidHex} (store={store.Name})");
        return id;
    }

    public static ISecretStore DefaultStore()
    {
        var store = new FilePermissionStore(FilePermissionStore.DefaultDirectory);
        if (!store.IsAvailable())
            throw new InvalidOperationException(
                $"cannot use {FilePermissionStore.DefaultDirectory} for key storage");
        return store;
    }

    /// Re-import through PKCS#12 so the private key is attached in a form
    /// SslStream can present for client authentication. On Linux the key
    /// must be loaded this way; a certificate carrying only a public key
    /// fails the TLS 1.3 client-auth handshake with an opaque error.
    private static X509Certificate2 LoadCert(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=clipsync", ecdsa, HashAlgorithmName.SHA256);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                        DateTimeOffset.UtcNow.AddYears(20));
        return LoadCert(cert.Export(X509ContentType.Pfx));
    }

    /// Diagnostic logging is opt-in (CLIPSYNC_DEBUG=1, or a `debug-enabled`
    /// file in the data directory) and must never include clipboard content
    /// or key material — only network/protocol metadata.
    ///
    /// Unlike Windows this writes to stderr rather than a log file: the
    /// daemon runs as a systemd --user service, so stderr is already
    /// captured, timestamped and rotated by the journal
    /// (`journalctl --user -u clipsync`). Adding our own file would mean
    /// reimplementing rotation badly alongside it.
    private static readonly object LogLock = new();
    private static bool? _logEnabled;

    /// Force diagnostic logging on regardless of env var / marker file.
    /// Called from Program.Main when `--debug` is passed.
    internal static void EnableLogging() => _logEnabled = true;

    internal static void Log(string msg)
    {
        try
        {
            _logEnabled ??= Environment.GetEnvironmentVariable("CLIPSYNC_DEBUG") == "1"
                            || File.Exists(Path.Combine(
                                   FilePermissionStore.DefaultDirectory, "debug-enabled"));
            if (_logEnabled != true) return;
            lock (LogLock)
                Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
        }
        catch { }
    }
}
