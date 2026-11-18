using System.Net;
using System.Net.Sockets;
using Tether.Shared;

namespace Tether.Service;

/// <summary>
/// Local DNS stub. Names on the block list get NXDOMAIN; everything else is forwarded to the
/// upstream servers captured at install. If the upstream is unreachable the child gets SERVFAIL
/// and the proxy keeps running.
///
/// <see cref="Ready"/> completes true once both IPv4 listeners (UDP and TCP) are bound and false if
/// either could not bind. Nothing may point adapters at this proxy before that, or a failed bind
/// would cut the laptop off the internet. IPv6 listeners are best effort.
/// </summary>
public sealed class DnsProxy
{
    private readonly BlockList _list;
    private readonly IPAddress[] _upstream;
    private readonly int _upstreamPort;
    private readonly int _upstreamTimeoutMs;
    private volatile IPAddress? _lastGood;
    private readonly Action<string> _onLookup;
    private readonly int _port;
    private readonly ILogger _log;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requiredPending = 2;

    public Task<bool> Ready => _ready.Task;

    public DnsProxy(BlockList list, IEnumerable<IPAddress> upstream, Action<string> onLookup, int port, ILogger log, int upstreamPort = 53, int upstreamTimeoutMs = 3000)
    {
        _upstreamPort = upstreamPort;
        _upstreamTimeoutMs = upstreamTimeoutMs;
        _list = list;
        _upstream = upstream.ToArray();
        _onLookup = onLookup;
        _port = port;
        _log = log;
        if (_upstream.Length == 0) _upstream = WithFallbacks(_upstream).ToArray();
    }

    private static readonly string[] PublicResolvers = { "1.1.1.1", "9.9.9.9" };

    /// <summary>The DNS servers captured at install usually belong to the home router, which is unreachable at school or on a hotspot, so public resolvers are always tried last.</summary>
    public static List<IPAddress> WithFallbacks(IEnumerable<IPAddress> configured)
    {
        var list = configured.Where(a => !IPAddress.IsLoopback(a)).Distinct().ToList();
        foreach (var r in PublicResolvers.Select(IPAddress.Parse)) if (!list.Contains(r)) list.Add(r);
        return list;
    }

    /// <summary>Runs until cancelled, or until any required listener stops (the caller restarts a fresh proxy).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new List<Task>
        {
            Task.Run(() => RunUdp(IPAddress.Loopback, true, cts.Token)),
            Task.Run(() => RunTcp(IPAddress.Loopback, true, cts.Token)),
            Task.Run(() => RunUdp(IPAddress.IPv6Loopback, false, cts.Token)),
            Task.Run(() => RunTcp(IPAddress.IPv6Loopback, false, cts.Token)),
        };
        await Task.WhenAny(tasks).ConfigureAwait(false);
        _ready.TrySetResult(false);
        cts.Cancel();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void Bound(bool required, bool ok)
    {
        if (!required) return;
        if (!ok) _ready.TrySetResult(false);
        else if (Interlocked.Decrement(ref _requiredPending) == 0) _ready.TrySetResult(true);
    }

    private async Task RunUdp(IPAddress addr, bool required, CancellationToken ct)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(new IPEndPoint(addr, _port));
            Bound(required, true);
            while (!ct.IsCancellationRequested)
            {
                var req = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    try { await udp.SendAsync(await Answer(req.Buffer, ct).ConfigureAwait(false), req.RemoteEndPoint, ct).ConfigureAwait(false); }
                    catch (Exception) { }
                }, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Bound(required, false);
            _log.LogError("DNS UDP listener on {Addr}:{Port} stopped: {Message}", addr, _port, e.Message);
        }
        finally { udp?.Dispose(); }
    }

    private async Task RunTcp(IPAddress addr, bool required, CancellationToken ct)
    {
        TcpListener? l = null;
        try
        {
            l = new TcpListener(addr, _port);
            l.Start();
            Bound(required, true);
            while (!ct.IsCancellationRequested)
            {
                var client = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => ServeTcp(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Bound(required, false);
            _log.LogError("DNS TCP listener on {Addr}:{Port} stopped: {Message}", addr, _port, e.Message);
        }
        finally { l?.Stop(); }
    }

    private async Task ServeTcp(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            var s = client.GetStream();
            var lenBuf = new byte[2];
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExact(s, lenBuf, ct).ConfigureAwait(false)) return;
                var len = (lenBuf[0] << 8) | lenBuf[1];
                var req = new byte[len];
                if (!await ReadExact(s, req, ct).ConfigureAwait(false)) return;
                var resp = await Answer(req, ct).ConfigureAwait(false);
                await s.WriteAsync(new[] { (byte)(resp.Length >> 8), (byte)resp.Length }, ct).ConfigureAwait(false);
                await s.WriteAsync(resp, ct).ConfigureAwait(false);
            }
        }
        catch (Exception) { }
    }

    private static async Task<bool> ReadExact(Stream s, byte[] buf, CancellationToken ct)
    {
        var n = 0;
        while (n < buf.Length)
        {
            var r = await s.ReadAsync(buf.AsMemory(n), ct).ConfigureAwait(false);
            if (r == 0) return false;
            n += r;
        }
        return true;
    }

    public async Task<byte[]> Answer(byte[] request, CancellationToken ct)
    {
        var q = DnsPacket.ParseQuestion(request);
        if (q == null) return DnsPacket.BuildServFail(request);
        _onLookup(q.Value.Name);
        if (_list.IsBlocked(q.Value.Name)) return DnsPacket.BuildNxDomain(request);
        var good = _lastGood;
        foreach (var up in _upstream.OrderBy(u => good != null && u.Equals(good) ? 0 : 1))
        {
            try
            {
                using var udp = new UdpClient(up.AddressFamily);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_upstreamTimeoutMs);
                await udp.SendAsync(request, new IPEndPoint(up, _upstreamPort), timeout.Token).ConfigureAwait(false);
                var resp = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                _lastGood = up;
                return resp.Buffer;
            }
            catch (Exception) { /* try the next upstream */ }
        }
        return DnsPacket.BuildServFail(request);
    }
}
