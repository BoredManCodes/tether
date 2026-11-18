using Tether.Shared;

namespace Tether.Tests;

public class UsageStoreTests
{
    [Fact]
    public void Add_accumulates_deltas_per_day_and_program()
    {
        var s = new UsageStore();
        s.Add(new[] { new UsageRow("2026-11-18", "a.exe", "A", 1000) });
        s.Add(new[] { new UsageRow("2026-11-18", "a.exe", "A", 2500), new UsageRow("2026-11-18", "b.exe", "B", 10) });
        var totals = s.Totals("2026-11-18");
        Assert.Equal(3500, totals["a.exe"]);
        Assert.Equal(10, totals["b.exe"]);
    }

    [Fact]
    public void Totals_for_another_day_are_separate()
    {
        var s = new UsageStore();
        s.Add(new[] { new UsageRow("2026-11-18", "a.exe", "A", 1000) });
        Assert.Empty(s.Totals("2026-11-19"));
    }

    [Fact]
    public void Rows_returns_cumulative_rows_for_the_server_to_replace_with()
    {
        var s = new UsageStore();
        s.Add(new[] { new UsageRow("2026-11-18", "a.exe", "App A", 1000) });
        s.Add(new[] { new UsageRow("2026-11-18", "a.exe", "App A", 1000) });
        var row = Assert.Single(s.Rows(new[] { "2026-11-18" }));
        Assert.Equal(("a.exe", "App A", 2000L), (row.Package, row.AppName, row.ForegroundMs));
    }

    [Fact]
    public void Save_then_Load_keeps_totals_and_a_corrupt_file_loads_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"usage-{Guid.NewGuid():N}.json");
        try
        {
            var s = new UsageStore();
            s.Add(new[] { new UsageRow("2026-11-18", "a.exe", "A", 1234) });
            s.Save(path);
            Assert.Equal(1234, UsageStore.Load(path).Totals("2026-11-18")["a.exe"]);
            File.WriteAllText(path, "{not json");
            Assert.Empty(UsageStore.Load(path).Totals("2026-11-18"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Days_older_than_14_are_dropped_when_new_usage_arrives()
    {
        var s = new UsageStore();
        s.Add(new[] { new UsageRow("2026-10-01", "a.exe", "A", 1000) });
        s.Add(new[] { new UsageRow("2026-11-18", "a.exe", "A", 1000) });
        Assert.Empty(s.Totals("2026-10-01"));
    }
}
