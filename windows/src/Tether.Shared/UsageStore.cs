using System.Globalization;
using System.Text.Json;

namespace Tether.Shared;

/// <summary>
/// Running foreground-time totals per day and program, kept by the service so the child's helper
/// cannot edit them. Saved under ProgramData (SYSTEM and Administrators only). Keeps 14 days.
/// </summary>
public sealed class UsageStore
{
    private readonly Dictionary<(string Day, string Package), (string AppName, long Ms)> _rows = new();
    private readonly object _lock = new();

    public void Add(IEnumerable<UsageRow> deltas)
    {
        lock (_lock)
        {
            foreach (var d in deltas)
            {
                var key = (d.Day, d.Package.ToLowerInvariant());
                _rows.TryGetValue(key, out var cur);
                _rows[key] = (d.AppName, cur.Ms + d.ForegroundMs);
            }
            Prune();
        }
    }

    private void Prune()
    {
        var days = _rows.Keys.Select(k => DateTime.TryParseExact(k.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null)
            .Where(d => d != null).Select(d => d!.Value).ToList();
        if (days.Count == 0) return;
        var cutoff = days.Max().AddDays(-14);
        foreach (var k in _rows.Keys.ToList())
            if (DateTime.TryParseExact(k.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < cutoff)
                _rows.Remove(k);
    }

    public Dictionary<string, long> Totals(string day)
    {
        lock (_lock)
            return _rows.Where(kv => kv.Key.Day == day).ToDictionary(kv => kv.Key.Package, kv => kv.Value.Ms);
    }

    /// <summary>Cumulative rows for the given days, in the shape the server's usage upsert replaces.</summary>
    public List<UsageRow> Rows(IEnumerable<string> days)
    {
        var set = new HashSet<string>(days);
        lock (_lock)
            return _rows.Where(kv => set.Contains(kv.Key.Day))
                .Select(kv => new UsageRow(kv.Key.Day, kv.Key.Package, kv.Value.AppName, kv.Value.Ms)).ToList();
    }

    public void Save(string path)
    {
        List<UsageRow> all;
        lock (_lock) all = _rows.Select(kv => new UsageRow(kv.Key.Day, kv.Key.Package, kv.Value.AppName, kv.Value.Ms)).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static UsageStore Load(string path)
    {
        var s = new UsageStore();
        try { s.Add(JsonSerializer.Deserialize<List<UsageRow>>(File.ReadAllText(path), Json.Options) ?? new()); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return s;
    }
}
