namespace Tether.Shared;

public static class AppBlockPlanner
{
    /// <summary>Running programs the parent's rules say to end. Tether's own programs are never listed.</summary>
    public static IReadOnlyList<string> ToEnd(IEnumerable<AppRule> rules, IReadOnlyDictionary<string, long> todayMs, IEnumerable<string> running)
    {
        var runningSet = new HashSet<string>(running.Select(r => r.ToLowerInvariant()));
        var result = new List<string>();
        foreach (var rule in rules)
        {
            var pkg = rule.Package.ToLowerInvariant();
            if (pkg.StartsWith("tether.") || !runningSet.Contains(pkg)) continue;
            todayMs.TryGetValue(pkg, out var ms);
            if (AppRuleEvaluator.ShouldBlock(rule, ms) && !result.Contains(pkg)) result.Add(pkg);
        }
        return result;
    }
}

public static class LockPolicy
{
    public static bool ShouldLock(IReadOnlyList<ScheduleWindow> windows, DateTime local, bool manualLock)
        => manualLock || ScheduleWindow.IsInside(windows, local);
}
