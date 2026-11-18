namespace Tether.Shared;

/// <summary>
/// Adds up foreground time per local day and program. <see cref="Drain"/> hands back the time
/// gained since the last drain (a delta); the service keeps the running daily totals so a helper
/// restart cannot lower what the server shows.
/// </summary>
public sealed class ForegroundTracker
{
    private readonly Func<DateTime> _localNow;
    private readonly Dictionary<(string Day, string Package), (string AppName, long Ms)> _delta = new();
    private readonly object _lock = new();

    public ForegroundTracker(Func<DateTime> localNow) => _localNow = localNow;

    /// <summary>Credits [now - elapsed, now] to the program, splitting at local midnight.</summary>
    public void Tick(string exe, string description, TimeSpan elapsed)
    {
        var end = _localNow();
        var start = end - elapsed;
        lock (_lock)
        {
            if (start.Date == end.Date)
            {
                Add(end.Date, exe, description, (long)elapsed.TotalMilliseconds);
                return;
            }
            var midnight = end.Date;
            Add(start.Date, exe, description, (long)(midnight - start).TotalMilliseconds);
            Add(end.Date, exe, description, (long)(end - midnight).TotalMilliseconds);
        }
    }

    private void Add(DateTime day, string exe, string description, long ms)
    {
        if (ms <= 0) return;
        var key = (day.ToString("yyyy-MM-dd"), exe.ToLowerInvariant());
        _delta.TryGetValue(key, out var cur);
        _delta[key] = (description, cur.Ms + ms);
    }

    public IReadOnlyList<UsageRow> Drain()
    {
        lock (_lock)
        {
            var rows = _delta.Select(kv => new UsageRow(kv.Key.Day, kv.Key.Package, kv.Value.AppName, kv.Value.Ms)).ToList();
            _delta.Clear();
            return rows;
        }
    }
}
