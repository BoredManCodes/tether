using Tether.Shared;

namespace Tether.Tests;

public class EnforcementTests
{
    private static readonly IReadOnlyDictionary<string, long> NoUsage = new Dictionary<string, long>();

    [Fact]
    public void Blocked_running_program_is_listed_to_end()
    {
        var rules = new[] { new AppRule("game.exe", true, null) };
        Assert.Equal(new[] { "game.exe" }, AppBlockPlanner.ToEnd(rules, NoUsage, new[] { "chrome.exe", "GAME.EXE" }));
    }

    [Fact]
    public void Program_over_its_daily_limit_is_listed_to_end()
    {
        var rules = new[] { new AppRule("game.exe", false, 30) };
        var used = new Dictionary<string, long> { ["game.exe"] = 30 * 60_000 };
        Assert.Equal(new[] { "game.exe" }, AppBlockPlanner.ToEnd(rules, used, new[] { "game.exe" }));
    }

    [Fact]
    public void Program_under_its_limit_or_not_running_is_left_alone()
    {
        var rules = new[] { new AppRule("game.exe", false, 30), new AppRule("other.exe", true, null) };
        var used = new Dictionary<string, long> { ["game.exe"] = 60_000 };
        Assert.Empty(AppBlockPlanner.ToEnd(rules, used, new[] { "game.exe", "chrome.exe" }));
    }

    [Fact]
    public void Tether_itself_is_never_ended()
    {
        var rules = new[] { new AppRule("tether.helper.exe", true, null) };
        Assert.Empty(AppBlockPlanner.ToEnd(rules, NoUsage, new[] { "tether.helper.exe" }));
    }

    [Fact]
    public void Lock_policy_locks_inside_a_window_or_after_a_manual_lock()
    {
        var w = new[] { new ScheduleWindow("3", 22 * 60, 23 * 60) };   // Wednesdays 22:00 to 23:00
        var inside = DateTime.Parse("2026-11-18 22:30");                // a Wednesday
        var outside = DateTime.Parse("2026-11-18 12:00");
        Assert.True(LockPolicy.ShouldLock(w, inside, manualLock: false));
        Assert.False(LockPolicy.ShouldLock(w, outside, manualLock: false));
        Assert.True(LockPolicy.ShouldLock(w, outside, manualLock: true));
    }
}
