using Tether.Shared;

namespace Tether.Tests;

public class ScreenStreamerTests
{
    private static (ScreenStreamer, List<byte[]>, Func<long>, Action<long>) Make()
    {
        long now = 1_000;
        var sent = new List<byte[]>();
        var s = new ScreenStreamer(() => new byte[] { 0xFF, 0xD8 }, b => { sent.Add(b); return Task.CompletedTask; }, () => now);
        return (s, sent, () => now, v => now = v);
    }

    [Fact]
    public async Task Tick_sends_a_frame_only_while_started()
    {
        var (s, sent, _, _) = Make();
        await s.TickAsync();
        Assert.Empty(sent);
        s.Start();
        await s.TickAsync();
        Assert.Single(sent);
        s.Stop();
        await s.TickAsync();
        Assert.Single(sent);
    }

    [Fact]
    public async Task Stream_stops_itself_after_thirty_minutes_without_a_stop_command()
    {
        var (s, sent, now, set) = Make();
        s.Start();
        set(now() + 30 * 60_000 - 1);
        await s.TickAsync();
        Assert.True(s.Active);
        set(now() + 1);
        await s.TickAsync();
        Assert.False(s.Active);
        Assert.Single(sent);
    }

    [Fact]
    public async Task CaptureOnce_sends_one_frame_without_starting_the_stream()
    {
        var (s, sent, _, _) = Make();
        await s.CaptureOnceAsync();
        Assert.Single(sent);
        Assert.False(s.Active);
    }

    [Fact]
    public async Task A_failing_sink_does_not_stop_the_stream()
    {
        long now = 0;
        var calls = 0;
        var s = new ScreenStreamer(() => new byte[1], _ => { calls++; throw new IOException("pipe closed"); }, () => now);
        s.Start();
        await s.TickAsync();
        await s.TickAsync();
        Assert.Equal(2, calls);
        Assert.True(s.Active);
    }
}
