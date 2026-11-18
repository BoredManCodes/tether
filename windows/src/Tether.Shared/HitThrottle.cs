namespace Tether.Shared;

/// <summary>Cooldowns from KeywordWatch.kt: one shot per 20 s, same word in same app once per 10 min.</summary>
public sealed class HitThrottle
{
    public const long ShotCooldownMs = 20_000;
    public const long ScreenRepeatMs = 10 * 60_000;

    private readonly Func<long> _nowMs;
    private long _lastShotAt = long.MinValue / 2;
    private readonly Dictionary<string, long> _reported = new();

    public HitThrottle(Func<long> nowMs) => _nowMs = nowMs;

    public bool ShouldShoot()
    {
        var now = _nowMs();
        if (now - _lastShotAt < ShotCooldownMs) return false;
        _lastShotAt = now;
        return true;
    }

    public bool ShouldReport(string word, string app)
    {
        var now = _nowMs();
        var key = word.ToLowerInvariant() + "|" + app.ToLowerInvariant();
        if (_reported.TryGetValue(key, out var last) && now - last < ScreenRepeatMs) return false;
        _reported[key] = now;
        return true;
    }
}
