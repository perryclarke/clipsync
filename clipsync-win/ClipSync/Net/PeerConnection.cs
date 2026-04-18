using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using PeterO.Cbor;
using ClipSync.Security;

namespace ClipSync.Net;

public enum PeerRole { Client, Server }

/// Wraps a TcpClient in an mTLS SslStream with SPKI pinning, then reads
/// length-prefixed CBOR frames. Symmetric for both roles.
public sealed class PeerConnection
{
    public Action? OnReady;
    public Action<ClipboardItem>? OnItem;
    public Action? OnClose;

    public byte[]? PeerDid { get; private set; }
    public string? PeerName { get; private set; }

    private readonly TcpClient _tcp;
    private readonly Identity _identity;
    private readonly TrustStore _trust;
    private readonly PeerRole _role;
    private SslStream? _ssl;

    public PeerConnection(TcpClient tcp, Identity identity, TrustStore trust, PeerRole role)
    {
        _tcp = tcp;
        _identity = identity;
        _trust = trust;
        _role = role;
    }

    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        try
        {
            _ssl = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false,
                userCertificateValidationCallback: ValidatePeer);

            if (_role == PeerRole.Client)
            {
                var opts = new SslClientAuthenticationOptions
                {
                    TargetHost = "clipsync",
                    ClientCertificates = new X509CertificateCollection { _identity.TlsCertificate },
                };
                await _ssl.AuthenticateAsClientAsync(opts);
            }
            else
            {
                var opts = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _identity.TlsCertificate,
                    ClientCertificateRequired = true,
                };
                await _ssl.AuthenticateAsServerAsync(opts);
            }

            await SendHelloAsync();
            await ReadLoopAsync();
        }
        catch { }
        finally
        {
            try { _ssl?.Dispose(); _tcp.Dispose(); } catch { }
            OnClose?.Invoke();
        }
    }

    private bool ValidatePeer(object? _, X509Certificate? cert, X509Chain? __, SslPolicyErrors ___)
    {
        if (cert is null) return false;
        var leaf = cert is X509Certificate2 c2 ? c2 : new X509Certificate2(cert);
        var did = Identity.ComputeDid(leaf);
        var hex = Convert.ToHexString(did).ToLowerInvariant();
        return _trust.Contains(hex);
    }

    public async Task SendAsync(byte[] frame)
    {
        if (_ssl is null) return;
        await _ssl.WriteAsync(frame);
        await _ssl.FlushAsync();
    }

    public Task SendItemAsync(ClipboardItem item) => SendAsync(Codec.EncodeClipboardItem(item));

    private async Task SendHelloAsync()
    {
        await SendAsync(Codec.EncodeHello(_identity.Did, Environment.MachineName,
            new[] { "text", "image", "files", "rich" }));
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[4];
        while (_ssl is { } s)
        {
            await ReadExactAsync(s, header, 0, 4);
            var len = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            if (len < 0 || len > Codec.MaxFrameSize) throw new InvalidDataException("oversize");
            var body = new byte[len];
            await ReadExactAsync(s, body, 0, len);
            var cbor = CBORObject.DecodeFromBytes(body);
            Handle(cbor);
        }
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, int off, int count)
    {
        while (count > 0)
        {
            var n = await s.ReadAsync(buf.AsMemory(off, count));
            if (n == 0) throw new EndOfStreamException();
            off += n; count -= n;
        }
    }

    private void Handle(CBORObject body)
    {
        var t = Codec.TypeOf(body);
        switch (t)
        {
            case MessageType.Hello:
                PeerDid = body["did"].GetByteString();
                PeerName = body.ContainsKey("name") ? body["name"].AsString() : null;
                OnReady?.Invoke();
                break;
            case MessageType.ClipboardItem:
                var item = Codec.DecodeClipboardItem(body);
                if (item is not null) OnItem?.Invoke(item);
                break;
        }
    }
}
