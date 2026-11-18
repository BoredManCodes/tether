using Tether.Shared;

namespace Tether.Tests;

public class AppRuleEvaluatorTests
{
    [Fact]
    public void Blocked_rule_always_blocks()
        => Assert.True(AppRuleEvaluator.ShouldBlock(new AppRule("game.exe", true, null), 0));

    [Fact]
    public void Daily_limit_blocks_at_the_limit_not_before()
    {
        var r = new AppRule("game.exe", false, 60);
        Assert.False(AppRuleEvaluator.ShouldBlock(r, 60 * 60_000 - 1));
        Assert.True(AppRuleEvaluator.ShouldBlock(r, 60 * 60_000));
    }

    [Fact]
    public void Rule_with_no_limit_and_not_blocked_allows()
        => Assert.False(AppRuleEvaluator.ShouldBlock(new AppRule("a.exe", false, null), 999_999_999));
}
