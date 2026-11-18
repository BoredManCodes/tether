namespace Tether.Shared;

public sealed record AppRule(string Package, bool Blocked, int? DailyLimitMin);

public static class AppRuleEvaluator
{
    public static bool ShouldBlock(AppRule rule, long todayForegroundMs)
        => rule.Blocked || (rule.DailyLimitMin is int m && m > 0 && todayForegroundMs >= m * 60_000L);
}
