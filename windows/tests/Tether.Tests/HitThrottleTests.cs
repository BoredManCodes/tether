using Tether.Shared;

namespace Tether.Tests;

public class HitThrottleTests
{
    [Fact]
    public void Shot_cooldown_is_twenty_seconds()
    {
        long now = 1_000_000;
        var t = new HitThrottle(() => now);
        Assert.True(t.ShouldShoot());
        now += 19_999;
        Assert.False(t.ShouldShoot());
        now += 1;
        Assert.True(t.ShouldShoot());
    }

    [Fact]
    public void Same_word_in_same_app_repeats_after_ten_minutes_only()
    {
        long now = 5_000_000;
        var t = new HitThrottle(() => now);
        Assert.True(t.ShouldReport("x", "chrome.exe"));
        now += 9 * 60_000;
        Assert.False(t.ShouldReport("x", "chrome.exe"));
        now += 60_000;
        Assert.True(t.ShouldReport("x", "chrome.exe"));
    }

    [Fact]
    public void Different_app_reports_immediately()
    {
        long now = 5_000_000;
        var t = new HitThrottle(() => now);
        Assert.True(t.ShouldReport("x", "chrome.exe"));
        Assert.True(t.ShouldReport("x", "msedge.exe"));
    }
}
