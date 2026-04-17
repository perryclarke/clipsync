using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Makaretu.Dns;
using ClipSync.Security;

namespace ClipSync.Net;

/// mDNS advertise + browse for `_clipsync._tcp` over IPv6, plus an
/// IPv6 TCP listener that PeerConnection drives.
public sealed class Discovery
{
    private readonly Identity _identity;
    private readonly TrustStore _trust;
    private readonly PeerRegistry _peers;
    private ServiceDiscovery? _sd;
    private TcpListener? _listener;

    public Discovery(Identity identity, TrustStore trust, PeerRegistry peers)
    {
        _identity = identity;
        _trust = trust;
        _peers = peers;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.IPv6Any, 0);
        _listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();

        _sd = new ServiceDiscovery();
        var profile = new ServiceProfile(Environment.MachineName, "_clipsync._tcp", (ushort)port);
        profile.AddProperty("v", "1");
        profile.AddProperty("did", _identity.DidHex);
        profile.AddProperty("name", Environment.MachineName);
        profile.AddProperty("caps", "text,image,files,rich");
        profile.AddProperty("pend", _trust.IsEmpty ? "1" : "0");
        _sd.Advertise(profile);

        _sd.ServiceInstanceDiscovered += async (_, e) =>
        {
            // Pull the TXT record from the additional records and decide
            // whether to connect. mDNS discovery in Makaretu yields
            // additional records we can query via the instance name.
            if (e.ServiceInstanceName.ToString().StartsWith(Environment.MachineName)) return;

            var didHex = TryReadProperty(e.Message, "did");
            if (didHex is null || !_trust.Contains(didHex)) return;
            if (_peers.IsConnected(didHex)) return;

            // Resolve AAAA records from the Additional section.
            foreach (var rr in e.Message.AdditionalRecords)
            {
                if (rr is AAAARecord aaaa)
                {
                    var srv = FindSrv(e.Message);
                    if (srv is null) continue;
                    await ConnectAsync(aaaa.Address, srv.Port);
                    break;
                }
            }
        };
        _sd.QueryServiceInstances("_clipsync._tcp");
    }

    private static SRVRecord? FindSrv(Message msg)
    {
        foreach (var rr in msg.AdditionalRecords)
            if (rr is SRVRecord srv) return srv;
        return null;
    }

    private static string? TryReadProperty(Message msg, string key)
    {
        foreach (var rr in msg.AdditionalRecords)
        {
            if (rr is TXTRecord txt)
            {
                foreach (var s in txt.Strings)
                {
                    var idx = s.IndexOf('=');
                    if (idx > 0 && s.Substring(0, idx) == key) return s.Substring(idx + 1);
                }
            }
        }
        return null;
    }

    private async Task AcceptLoop()
    {
        while (_listener is { } l)
        {
            TcpClient? c;
            try { c = await l.AcceptTcpClientAsync(); } catch { break; }
            _ = Task.Run(() =>
            {
                var pc = new PeerConnection(c, _identity, _trust, PeerRole.Server);
                _peers.Adopt(pc);
                pc.Start();
            });
        }
    }

    private async Task ConnectAsync(IPAddress addr, int port)
    {
        var c = new TcpClient(AddressFamily.InterNetworkV6);
        await c.ConnectAsync(addr, port);
        var pc = new PeerConnection(c, _identity, _trust, PeerRole.Client);
        _peers.Adopt(pc);
        pc.Start();
    }
}
