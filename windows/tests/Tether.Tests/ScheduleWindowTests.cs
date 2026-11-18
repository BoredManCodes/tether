using Tether.Shared;

namespace Tether.Tests;

public class ScheduleWindowTests
{
    // Days is a digit string, 0=Sunday..6=Saturday, like the server and ScheduleManager.kt.
    private static readonly ScheduleWindow[] Friday2200To0600 = { new("5", 22 * 60, 6 * 60) };

    [Theory]
    [InlineData("2026-11-20 23:00", true)]   // Friday night
    [InlineData("2026-11-21 05:00", true)]   // Saturday early, tail of Friday's window
    [InlineData("2026-11-21 07:00", false)]
    [InlineData("2026-11-20 12:00", false)]
    [InlineData("2026-11-22 05:00", false)]  // Sunday early: Saturday has no window
    public void Midnight_wrap_follows_the_previous_day(string local, bool inside)
        => Assert.Equal(inside, ScheduleWindow.IsInside(Friday2200To0600, DateTime.Parse(local)));

    [Fact]
    public void Same_day_window_ends_exclusive()
    {
        var w = new[] { new ScheduleWindow("1", 9 * 60, 10 * 60) };
        Assert.True(ScheduleWindow.IsInside(w, DateTime.Parse("2026-11-16 09:59")));
        Assert.False(ScheduleWindow.IsInside(w, DateTime.Parse("2026-11-16 10:00")));
    }

    [Fact]
    public void Uses_wall_clock_minutes_on_a_dst_day()
    {
        // 2026-10-04 is a Sunday; Australian DST starts then. Wall-clock 23:00 is still inside a 22:00 window.
        var w = new[] { new ScheduleWindow("0", 22 * 60, 23 * 60 + 30) };
        Assert.True(ScheduleWindow.IsInside(w, new DateTime(2026, 10, 4, 23, 0, 0, DateTimeKind.Local)));
    }
}
