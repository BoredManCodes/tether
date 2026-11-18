using Tether.Shared;

namespace Tether.Tests;

public class ForegroundTrackerTests
{
    [Fact]
    public void Ticks_around_local_midnight_split_into_two_day_keys()
    {
        var now = new DateTime(2026, 11, 19, 0, 0, 2, DateTimeKind.Local);
        var t = new ForegroundTracker(() => now);
        t.Tick("chrome.exe", "Chrome", TimeSpan.FromSeconds(5));
        var rows = t.Drain().OrderBy(r => r.Day).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("2026-11-18", 3000L), (rows[0].Day, rows[0].ForegroundMs));
        Assert.Equal(("2026-11-19", 2000L), (rows[1].Day, rows[1].ForegroundMs));
    }

    [Fact]
    public void A_dst_day_still_totals_the_elapsed_time()
    {
        var now = new DateTime(2026, 10, 4, 12, 0, 5, DateTimeKind.Local);
        var t = new ForegroundTracker(() => now);
        t.Tick("chrome.exe", "Chrome", TimeSpan.FromSeconds(5));
        t.Tick("chrome.exe", "Chrome", TimeSpan.FromSeconds(5));
        var row = Assert.Single(t.Drain());
        Assert.Equal(("2026-10-04", "chrome.exe", 10_000L), (row.Day, row.Package, row.ForegroundMs));
    }

    [Fact]
    public void Drain_returns_time_since_the_last_drain_not_the_running_total()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Local);
        var t = new ForegroundTracker(() => now);
        t.Tick("a.exe", "A", TimeSpan.FromSeconds(5));
        Assert.Equal(5000, Assert.Single(t.Drain()).ForegroundMs);
        Assert.Empty(t.Drain());
        t.Tick("a.exe", "A", TimeSpan.FromSeconds(5));
        Assert.Equal(5000, Assert.Single(t.Drain()).ForegroundMs);
    }

    [Fact]
    public void Different_programs_get_their_own_rows_with_their_description()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Local);
        var t = new ForegroundTracker(() => now);
        t.Tick("a.exe", "App A", TimeSpan.FromSeconds(5));
        t.Tick("b.exe", "App B", TimeSpan.FromSeconds(5));
        var rows = t.Drain().OrderBy(r => r.Package).ToList();
        Assert.Equal(new[] { ("a.exe", "App A"), ("b.exe", "App B") }, rows.Select(r => (r.Package, r.AppName)));
    }
}
