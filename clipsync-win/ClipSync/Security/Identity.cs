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

    public byte[] Did { get; }              // SPKI SHA-256 of TLS cert
    public string DidHex => Convert.ToHexString(Did).ToLowerInvariant();
    public X509Certificate2 TlsCertificate { get; }
    public Ed25519PrivateKeyParameters Ed25519Private { get; }

    private Identity(byte[] did, X509Certificate2 cert, Ed25519PrivateKeyParameters ed)
    {
        Did = did; TlsCertificate = cert; Ed25519Private = ed;
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
            cert = new X509Certificate2(blob, (string?)null,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
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

        var spki = SHA256.HashData(cert.PublicKey.EncodedKeyValue.RawData);
        var id = new Identity(spki, cert, ed);
        Current = id;
        return id;
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=clipsync", ecdsa, HashAlgorithmName.SHA256);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                        DateTimeOffset.UtcNow.AddYears(20));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
    }
}
