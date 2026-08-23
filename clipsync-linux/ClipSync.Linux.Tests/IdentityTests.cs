using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClipSync.Platform.Backends;
using ClipSync.Security;
using Xunit;

namespace ClipSync.Linux.Tests;

/// The device id is the one thing that absolutely must match the mac and
/// Windows clients byte for byte. Everything downstream — pinning, the trust
/// store, the TOFU flow, the whole peer identity — is derived from it, and a
/// wrong id fails as "the peer never connects", with nothing in any log to
/// say why. So it is pinned here from several directions.
public class IdentityTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// The public certificate only. No private key is committed: the device
    /// id is derived from the public point, so the key would add nothing but
    /// a secret in the repository.
    private static X509Certificate2 GoldenCert() =>
        X509Certificate2.CreateFromPem(File.ReadAllText(FixturePath("golden-cert.pem")));

    /// The golden vector. This expected value was NOT produced by this code:
    /// the public point was extracted with `openssl ec -pubin -text` and
    /// hashed with coreutils `sha256sum`, so the test compares .NET against
    /// an independent toolchain rather than against itself.
    private const string GoldenDid =
        "557d95a190560457215a3045e607167dd326a1bb83f16ec5d42869490e0baa79";

    [Fact]
    public void ComputeDid_MatchesGoldenVector()
    {
        using var cert = GoldenCert();
        Assert.Equal(GoldenDid, Convert.ToHexString(Identity.ComputeDid(cert)).ToLowerInvariant());
    }

    /// Independent derivation inside .NET: rebuild the X9.63 uncompressed
    /// point from the curve parameters rather than trusting
    /// EncodedKeyValue.RawData to be what we think it is. If a future .NET
    /// changes that property's meaning, this catches it.
    [Fact]
    public void ComputeDid_MatchesPointRebuiltFromCurveParameters()
    {
        using var cert = GoldenCert();
        using var ecdsa = cert.GetECDsaPublicKey()!;
        var p = ecdsa.ExportParameters(false);

        var point = new byte[1 + p.Q.X!.Length + p.Q.Y!.Length];
        point[0] = 0x04;                                    // uncompressed
        p.Q.X.CopyTo(point, 1);
        p.Q.Y.CopyTo(point, 1 + p.Q.X.Length);

        Assert.Equal(65, point.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(point)).ToLowerInvariant(),
                     Convert.ToHexString(Identity.ComputeDid(cert)).ToLowerInvariant());
    }

    /// PROTOCOL.md §2 says the id is the hash of the SPKI. It is not, and
    /// both shipping clients hash the raw point instead. This test exists so
    /// that anyone "correcting" ComputeDid to match the spec breaks a test
    /// that explains why it is wrong, rather than shipping a client that
    /// silently cannot pair with anything. See HANDOFF.md.
    [Fact]
    public void ComputeDid_IsNotTheSpkiHash_AsProtocolMdClaims()
    {
        using var cert = GoldenCert();
        var spkiHash = SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo());
        Assert.NotEqual(Convert.ToHexString(spkiHash).ToLowerInvariant(),
                        Convert.ToHexString(Identity.ComputeDid(cert)).ToLowerInvariant());
    }

    /// The id must survive the PKCS#12 export/import that persistence uses,
    /// or the device would come back with a different identity after every
    /// restart and every peer would have to re-trust it.
    [Fact]
    public void ComputeDid_IsStableAcrossPkcs12RoundTrip()
    {
        using var cert = GoldenCert();
        var before = Identity.ComputeDid(cert);
        using var reloaded = X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        Assert.Equal(before, Identity.ComputeDid(reloaded));
    }

    [Fact]
    public void LoadOrCreate_IsStableAcrossReload()
    {
        using var tmp = new TempStore();
        var first = Identity.LoadOrCreate(tmp.Store);
        var second = Identity.LoadOrCreate(tmp.Store);

        Assert.Equal(first.DidHex, second.DidHex);
        Assert.Equal(first.Ed25519Private.GetEncoded(), second.Ed25519Private.GetEncoded());
    }

    /// TLS 1.3 client authentication needs the private key attached to the
    /// certificate SslStream presents. On Linux a cert loaded without it
    /// fails the handshake with an error that says nothing useful, so this
    /// is checked directly rather than discovered during interop testing.
    [Fact]
    public void LoadOrCreate_ProducesCertificateWithUsablePrivateKey()
    {
        using var tmp = new TempStore();
        var identity = Identity.LoadOrCreate(tmp.Store);

        Assert.True(identity.TlsCertificate.HasPrivateKey);
        Assert.NotNull(identity.TlsCertificate.GetECDsaPrivateKey());
    }

    [Fact]
    public void LoadOrCreate_DidIsSixtyFourHexChars()
    {
        using var tmp = new TempStore();
        var identity = Identity.LoadOrCreate(tmp.Store);

        Assert.Equal(64, identity.DidHex.Length);
        Assert.Equal(identity.DidHex, identity.DidHex.ToLowerInvariant());
        Assert.Equal(32, identity.Did.Length);
    }

    /// Key material must not be readable by other users on the machine.
    [Fact]
    public void SecretStore_WritesOwnerOnlyFiles()
    {
        using var tmp = new TempStore();
        Identity.LoadOrCreate(tmp.Store);

        foreach (var name in new[] { "tls.pfx", "ed25519.key" })
        {
            var path = Path.Combine(tmp.Dir, name);
            Assert.True(File.Exists(path), $"{name} was not written");
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }
}

/// A FilePermissionStore rooted in a throwaway directory, so tests never
/// touch the real identity in the user's home.
internal sealed class TempStore : IDisposable
{
    public string Dir { get; }
    public FilePermissionStore Store { get; }

    public TempStore()
    {
        Dir = Path.Combine(Path.GetTempPath(), "clipsync-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Store = new FilePermissionStore(Dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { }
    }
}
