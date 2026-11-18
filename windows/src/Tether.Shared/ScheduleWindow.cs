namespace Tether.Shared;

/// <summary>
/// One bedtime window as the server stores it: Days is a digit string (0=Sunday..6=Saturday),
/// times are minutes since local midnight, and EndMin &lt;= StartMin wraps past midnight.
/// Mirrors ScheduleManager.kt.
/// </summary>
public sealed record ScheduleWindow(string Days, int StartMin, int EndMin)
{
    public static bool IsInside(IReadOnlyList<ScheduleWindow> windows, DateTime local)
    {
        var today = (int)local.DayOfWeek;
        var yesterday = (today + 6) % 7;
        var minute = local.Hour * 60 + local.Minute;
        foreach (var w in windows)
        {
            var onToday = w.Days.Contains((char)('0' + today));
            var onYesterday = w.Days.Contains((char)('0' + yesterday));
            var inside = w.EndMin > w.StartMin
                ? onToday && minute >= w.StartMin && minute < w.EndMin
                : (onToday && minute >= w.StartMin) || (onYesterday && minute < w.EndMin);
            if (inside) return true;
        }
        return false;
    }
}
