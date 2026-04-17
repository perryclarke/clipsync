using System;
using System.Security.Cryptography;
using System.Text;

namespace ClipSync.Security;

/// Matches macOS EnrollmentSession: HKDF → session key → HMAC confirm
/// tags → per-direction AES-GCM sub-keys. SPAKE2 group math (RFC 9383
/// edwards25519) is left as a TODO; slot its output secret into
/// Derive().
public sealed class EnrollmentSession
{
    public enum Side { Initiator, Responder }

    public Side Who { get; }
    public string Pin { get; }
    public byte[] OurDid { get; }
    public byte[] TheirDid { get; }
    public byte[] Salt { get; }
    public byte[]? SessionKey { get; private set; }

    public EnrollmentSession(Side who, string pin, byte[] ourDid, byte[] theirDid, byte[] salt)
    {
        Who = who; Pin = pin; OurDid = ourDid; TheirDid = theirDid; Salt = salt;
    }

    public void Derive(byte[] spake2Secret)
    {
        var prk = HMACSHA256.HashData(Salt, spake2Secret);
        SessionKey = HMACSHA256.HashData(prk, Encoding.UTF8.GetBytes("clipsync-enroll-v1"));
    }

    public (byte[] A, byte[] B) ConfirmTags()
    {
        if (SessionKey is null) throw new InvalidOperationException();
        var a = HMACSHA256.HashData(SessionKey, Encoding.UTF8.GetBytes("clipsync-confirm-A"));
        var b = HMACSHA256.HashData(SessionKey, Encoding.UTF8.GetBytes("clipsync-confirm-B"));
        return (a, b);
    }

    public byte[] Seal(byte[] plaintext, string direction)
    {
        if (SessionKey is null) throw new InvalidOperationException();
        var subkey = HMACSHA256.HashData(SessionKey, Encoding.UTF8.GetBytes(direction));
        var nonce = new byte[12];
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(subkey, 16);
        gcm.Encrypt(nonce, plaintext, ct, tag);
        var outBuf = new byte[ct.Length + 16];
        Buffer.BlockCopy(ct, 0, outBuf, 0, ct.Length);
        Buffer.BlockCopy(tag, 0, outBuf, ct.Length, 16);
        return outBuf;
    }

    public byte[] Open(byte[] sealed_, string direction)
    {
        if (SessionKey is null) throw new InvalidOperationException();
        var subkey = HMACSHA256.HashData(SessionKey, Encoding.UTF8.GetBytes(direction));
        var nonce = new byte[12];
        var ct = new byte[sealed_.Length - 16];
        var tag = new byte[16];
        Buffer.BlockCopy(sealed_, 0, ct, 0, ct.Length);
        Buffer.BlockCopy(sealed_, ct.Length, tag, 0, 16);
        var pt = new byte[ct.Length];
        using var gcm = new AesGcm(subkey, 16);
        gcm.Decrypt(nonce, ct, tag, pt);
        return pt;
    }
}

// TODO: port an RFC 9383 SPAKE2 over edwards25519 implementation (e.g.
// from the Matter/CHIP C++ reference or Go x/crypto). Pipe its shared
// secret into EnrollmentSession.Derive().
