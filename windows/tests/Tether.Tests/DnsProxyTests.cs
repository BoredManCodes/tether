using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Tether.Service;
using Tether.Shared;

namespace Tether.Tests;

public class DnsProxyTests
{
    private static readonly byte[] BlockedQuery =
    {
        0xAB, 0xCD, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        3, (byte)'b', (byte)'a', (byte)'d', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0x00, 0x01, 0x00, 0x01,
    };

    private static int FreePort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task Ready_is_true_when_the_port_binds_and_blocked_names_get_nxdomain()
    {
        var list = new BlockList();
        list.Load("bad.com");
        var looked = new List<string>();
        using var cts = new CancellationTokenSource();
        var proxy = new DnsProxy(list, new[] { IPAddress.Parse("192.0.2.1") }, looked.Add, FreePort(), NullLogger.Instance);
        var run = proxy.RunAsync(cts.Token);
        Assert.True(await proxy.Ready.WaitAsync(TimeSpan.FromSeconds(5)));
        var reply = await proxy.Answer(BlockedQuery, cts.Token);
        Assert.Equal(3, reply[3] & 0x0F);
        Assert.Equal(new[] { "bad.com" }, looked);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Ready_is_false_when_the_port_is_already_taken()
    {
        var port = FreePort();
        using var squatter = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        using var cts = new CancellationTokenSource();
        var proxy = new DnsProxy(new BlockList(), new[] { IPAddress.Parse("192.0.2.1") }, _ => { }, port, NullLogger.Instance);
        var run = proxy.RunAsync(cts.Token);
        Assert.False(await proxy.Ready.WaitAsync(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Unreachable_upstream_gives_servfail_not_an_exception()
    {
        using var cts = new CancellationTokenSource();
        var proxy = new DnsProxy(new BlockList(), new[] { IPAddress.Parse("192.0.2.1") }, _ => { }, FreePort(), NullLogger.Instance);
        var reply = await proxy.Answer(BlockedQuery, cts.Token);
        Assert.Equal(2, reply[3] & 0x0F);
    }

    [Fact]
    public void WithFallbacks_keeps_configured_servers_first_then_adds_public_resolvers_once()
    {
        var list = DnsProxy.WithFallbacks(new[] { IPAddress.Parse("192.168.0.1"), IPAddress.Parse("127.0.0.1"), IPAddress.Parse("1.1.1.1") });
        Assert.Equal(new[] { "192.168.0.1", "1.1.1.1", "9.9.9.9" }, list.Select(a => a.ToString()));
    }

    [Fact]
    public async Task Answer_remembers_the_server_that_worked_so_a_dead_home_router_costs_one_timeout_not_every_lookup()
    {
        using var fake = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var fakePort = ((IPEndPoint)fake.Client.LocalEndPoint!).Port;
        using var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var r = await fake.ReceiveAsync(cts.Token);
                var reply = (byte[])r.Buffer.Clone();
                reply[2] |= 0x80;   // QR: response
                await fake.SendAsync(reply, r.RemoteEndPoint, cts.Token);
            }
        }, cts.Token);

        var proxy = new DnsProxy(new BlockList(), new[] { IPAddress.Parse("192.0.2.1"), IPAddress.Loopback }, _ => { }, FreePort(), NullLogger.Instance, upstreamPort: fakePort, upstreamTimeoutMs: 400);
        var first = await proxy.Answer(BlockedQuery, cts.Token);
        Assert.Equal(0, first[3] & 0x0F);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await proxy.Answer(BlockedQuery, cts.Token);
        Assert.True(sw.ElapsedMilliseconds < 300, $"second lookup took {sw.ElapsedMilliseconds} ms");
        cts.Cancel();
    }
}
