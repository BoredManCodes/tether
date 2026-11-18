namespace Tether.Shared;

/// <summary>
/// Live view driver. The host calls <see cref="TickAsync"/> once a second; while started each tick
/// captures and sends one frame. A stream with no stop command ends itself after 30 minutes.
/// A failing sink (pipe closed, server down) never stops the stream; the next tick tries again.
/// </summary>
public sealed class ScreenStreamer
{
    public const long MaxStreamMs = 30 * 60_000;

    private readonly Func<byte[]> _capture;
    private readonly Func<byte[], Task> _sink;
    private readonly Func<long> _nowMs;
    private long _startedAt;

    public bool Active { get; private set; }

    public ScreenStreamer(Func<byte[]> capture, Func<byte[], Task> sink, Func<long> nowMs)
    {
        _capture = capture;
        _sink = sink;
        _nowMs = nowMs;
    }

    public void Start()
    {
        _startedAt = _nowMs();
        Active = true;
    }

    public void Stop() => Active = false;

    public async Task TickAsync()
    {
        if (!Active) return;
        if (_nowMs() - _startedAt >= MaxStreamMs) { Active = false; return; }
        await SendOne().ConfigureAwait(false);
    }

    public Task CaptureOnceAsync() => SendOne();

    private async Task SendOne()
    {
        try { await _sink(_capture()).ConfigureAwait(false); }
        catch (Exception) { /* keep streaming; the next tick retries */ }
    }
}
