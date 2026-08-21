using System;
using System.Collections.Generic;
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
    /// Capabilities the peer advertised in its Hello. "stream" means it
    /// reassembles FileChunk/FileEnd; without it, formats over the inline
    /// limit are dropped rather than streamed (StreamPlanner).
    public IReadOnlySet<string> PeerCaps => _peerCaps;
    public bool PeerStreams => _peerCaps.Contains("stream");
    public PeerRole Role => _role;

    private readonly TcpClient _tcp;
    private readonly Identity _identity;
    private readonly TrustStore _trust;
    private readonly PeerRole _role;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SslStream? _ssl;
    private byte[]? _verifiedDid;
    private HashSet<string> _peerCaps = new(StringComparer.Ordinal);
    private DateTime _lastRx = DateTime.UtcNow;
    private volatile bool _closed;

    /// Per-connection stream_id source for formats we stream out.
    private ulong _nextStreamId = 1;
    /// Reassembly of streamed items the peer sends us. Only touched from
    /// the read loop (and the ping loop's stale check), so no lock beyond
    /// the assembler's own single-threaded contract is needed there.
    private readonly StreamAssembler _assembler = new();
    private readonly object _assemblerLock = new();

    public static readonly string[] LocalCaps = { "text", "image", "files", "rich", "stream" };

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

    /// Put a locally built (all-inline) item on the wire: small formats
    /// inline, large ones as FileChunk/FileEnd streams after the item
    /// frame, per StreamPlanner. The whole sequence goes out under the
    /// write lock so nothing interleaves with it. See PROTOCOL.md §6.5, §10.
    public async Task SendItemAsync(ClipboardItem item)
    {
        if (_ssl is null || _closed) return;
        StreamPlanner.Result plan;
        lock (_assemblerLock) plan = StreamPlanner.Plan(item, PeerStreams, () => _nextStreamId++);
        foreach (var d in plan.Dropped)
            Identity.Log($"PeerConnection: dropping {d.Mime} ({d.Size / (1024.0 * 1024.0):F1} MB): {d.Reason}");
        if (plan.WireItem is null)
        {
            Identity.Log("PeerConnection: nothing of the item fits, not sending");
            return;
        }

        await _writeLock.WaitAsync();
        try
        {
            await _ssl.WriteAsync(Codec.EncodeClipboardItem(plan.WireItem));
            foreach (var st in plan.Streams)
            {
                Identity.Log($"PeerConnection: streaming {st.Data.Length / (1024.0 * 1024.0):F1} MB as stream {st.StreamId}");
                for (var off = 0; off < st.Data.Length; off += StreamPlanner.ChunkBytes)
                {
                    var n = Math.Min(StreamPlanner.ChunkBytes, st.Data.Length - off);
                    await _ssl.WriteAsync(Codec.EncodeFileChunk(st.StreamId, (ulong)off, st.Data.AsSpan(off, n)));
                }
                await _ssl.WriteAsync(Codec.EncodeFileEnd(st.StreamId, (ulong)st.Data.Length, SHA256.HashData(st.Data)));
            }
            await _ssl.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    private async Task SendHelloAsync()
    {
        await SendAsync(Codec.EncodeHello(_identity.Did, Environment.MachineName, LocalCaps));
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
                lock (_assemblerLock)
                {
                    if (_assembler.DropStale(DateTime.UtcNow))
                        Identity.Log("PeerConnection: dropped a streamed item with no progress for 30 s");
                }
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
                _peerCaps = Codec.DecodeHelloCaps(body);
                Identity.Log($"Hello from: name={PeerName}, did={Convert.ToHexString(PeerDid).ToLowerInvariant()}");
                OnReady?.Invoke();
                break;
            case MessageType.ClipboardItem:
                var item = Codec.DecodeClipboardItem(body);
                if (item is null) break;
                if (!StreamAssembler.NeedsAssembly(item)) { OnItem?.Invoke(item); break; }
                lock (_assemblerLock)
                {
                    var r = _assembler.Park(item, DateTime.UtcNow);
                    if (r.Reason is { } why) Identity.Log($"PeerConnection: park streamed item: {r.Outcome} ({why})");
                }
                break;
            case MessageType.FileChunk:
                if (Codec.DecodeFileChunk(body) is { } ch)
                {
                    ClipboardItem? done = null;
                    lock (_assemblerLock)
                    {
                        var r = _assembler.Chunk(ch.StreamId, ch.Offset, ch.Data, DateTime.UtcNow);
                        if (r.Outcome != StreamOutcome.Ok) Identity.Log($"PeerConnection: chunk: {r.Outcome} ({r.Reason})");
                        done = _assembler.TakeCompleted();
                    }
                    if (done is not null) OnItem?.Invoke(done);
                }
                break;
            case MessageType.FileEnd:
                if (Codec.DecodeFileEnd(body) is { } end)
                {
                    ClipboardItem? done = null;
                    lock (_assemblerLock)
                    {
                        var r = _assembler.End(end.StreamId, end.TotalSize, end.Sha256, DateTime.UtcNow);
                        if (r.Outcome != StreamOutcome.Ok) Identity.Log($"PeerConnection: end: {r.Outcome} ({r.Reason})");
                        done = _assembler.TakeCompleted();
                    }
                    if (done is not null)
                    {
                        Identity.Log($"PeerConnection: streamed item complete ({done.Formats.Count} formats)");
                        OnItem?.Invoke(done);
                    }
                }
                break;
            case MessageType.Ping:
                _ = SendAsync(Codec.EncodePong());
                break;
            case MessageType.Pong:
                break;
        }
    }
}
