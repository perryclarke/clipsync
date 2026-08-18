using System;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
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
    public PeerRole Role => _role;

    private readonly TcpClient _tcp;
    private readonly Identity _identity;
    private readonly TrustStore _trust;
    private readonly PeerRole _role;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SslStream? _ssl;
    private byte[]? _verifiedDid;
    private DateTime _lastRx = DateTime.UtcNow;
    private volatile bool _closed;

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(75);

    public PeerConnection(TcpClient tcp, Identity identity, TrustStore trust, PeerRole role)
    {
        _tcp = tcp;
        _identity = identity;
        _trust = trust;
        _role = role;
    }

    public void Start() => _ = RunAsync();

    public void Close()
    {
        _closed = true;
        try { _ssl?.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
    }

    private async Task RunAsync()
    {
        try
        {
            Identity.Log($"PeerConnection.Run: role={_role}, remote={_tcp.Client.RemoteEndPoint}");
            ConfigureKeepAlive(_tcp.Client);
            _ssl = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false,
                userCertificateValidationCallback: ValidatePeer);

            using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (_role == PeerRole.Client)
            {
                var opts = new SslClientAuthenticationOptions
                {
                    TargetHost = "clipsync",
                    ClientCertificates = new X509CertificateCollection { _identity.TlsCertificate },
                    EnabledSslProtocols = SslProtocols.Tls13,
                };
                await _ssl.AuthenticateAsClientAsync(opts, handshakeCts.Token);
            }
            else
            {
                var opts = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _identity.TlsCertificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls13,
                };
                await _ssl.AuthenticateAsServerAsync(opts, handshakeCts.Token);
            }

            // The pinned cert, not the (unauthenticated) Hello message, is
            // the source of truth for who we're talking to. Bind PeerDid
            // here, before any frame is read, so nothing keyed on it (the
            // registry, the per-peer mute) can ever see a self-asserted
            // value; the Hello's did is only checked for agreement below.
            if (_ssl.RemoteCertificate is not { } remoteCert)
                throw new AuthenticationException("no peer certificate after handshake");
            var leaf = remoteCert as X509Certificate2 ?? new X509Certificate2(remoteCert);
            _verifiedDid = Identity.ComputeDid(leaf);
            PeerDid = _verifiedDid;

            Identity.Log($"PeerConnection: TLS handshake complete, role={_role}");
            await SendHelloAsync();
            _ = PingLoopAsync();
            await ReadLoopAsync();
        }
        catch (Exception ex)
        {
            Identity.Log($"PeerConnection error ({_role}): {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _closed = true;
            try { _ssl?.Dispose(); _tcp.Dispose(); } catch { }
            OnClose?.Invoke();
        }
    }

    private static void ConfigureKeepAlive(Socket s)
    {
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch { /* keepalive is best-effort */ }
    }

    private bool ValidatePeer(object? _, X509Certificate? cert, X509Chain? __, SslPolicyErrors ___)
    {
        if (cert is null)
        {
            Identity.Log("ValidatePeer: cert is null → reject");
            return false;
        }
        var leaf = cert is X509Certificate2 c2 ? c2 : new X509Certificate2(cert);
        var did = Identity.ComputeDid(leaf);
        var hex = Convert.ToHexString(did).ToLowerInvariant();
        var trusted = _trust.Contains(hex);
        Identity.Log($"ValidatePeer: peer did={hex}, trusted={trusted}");
        return trusted;
    }

    public async Task SendAsync(byte[] frame)
    {
        if (_ssl is null || _closed) return;
        await _writeLock.WaitAsync();
        try
        {
            await _ssl.WriteAsync(frame);
            await _ssl.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    public Task SendItemAsync(ClipboardItem item) => SendAsync(Codec.EncodeClipboardItem(item));

    private async Task SendHelloAsync()
    {
        await SendAsync(Codec.EncodeHello(_identity.Did, Environment.MachineName,
            new[] { "text", "image", "files", "rich" }));
    }

    /// App-level keepalive: detects dead links (sleep, AP roam) that TCP
    /// alone can take many minutes to notice, so the registry stays
    /// accurate and the reconnect loop can kick in.
    private async Task PingLoopAsync()
    {
        try
        {
            while (!_closed)
            {
                await Task.Delay(PingInterval);
                if (_closed) return;
                if (DateTime.UtcNow - _lastRx > IdleTimeout)
                {
                    Identity.Log($"PeerConnection: keepalive timeout ({_role}), closing");
                    Close();
                    return;
                }
                await SendAsync(Codec.EncodePing());
            }
        }
        catch { Close(); }
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
            _lastRx = DateTime.UtcNow;
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
                var claimed = body["did"].GetByteString();
                if (_verifiedDid is null || !claimed.SequenceEqual(_verifiedDid))
                    throw new InvalidDataException("hello DID does not match TLS certificate");
                PeerDid = _verifiedDid;
                PeerName = body.ContainsKey("name") ? body["name"].AsString() : null;
                Identity.Log($"Hello from: name={PeerName}, did={Convert.ToHexString(PeerDid).ToLowerInvariant()}");
                OnReady?.Invoke();
                break;
            case MessageType.ClipboardItem:
                var item = Codec.DecodeClipboardItem(body);
                if (item is not null) OnItem?.Invoke(item);
                break;
            case MessageType.Ping:
                _ = SendAsync(Codec.EncodePong());
                break;
            case MessageType.Pong:
                break;
        }
    }
}
