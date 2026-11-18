namespace Tether.Shared;

/// <summary>
/// The last 64 typed characters, in memory only. Cleared by <see cref="Reset"/> (window change)
/// and when 3 seconds pass without a key. Nothing here is ever stored or uploaded.
/// </summary>
public sealed class KeyboardBuffer
{
    public const int Capacity = 64;
    public const long IdleMs = 3_000;

    private readonly Func<long> _nowMs;
    private readonly System.Text.StringBuilder _sb = new();
    private long _lastPush;
    private readonly object _lock = new();

    public KeyboardBuffer(Func<long> nowMs) => _nowMs = nowMs;

    public void Push(char c)
    {
        lock (_lock)
        {
            var now = _nowMs();
            if (_sb.Length > 0 && now - _lastPush >= IdleMs) _sb.Clear();
            _lastPush = now;
            if (c == '\b') { if (_sb.Length > 0) _sb.Length--; return; }
            if (char.IsControl(c) && c != '\t') { return; }
            _sb.Append(c);
            if (_sb.Length > Capacity) _sb.Remove(0, _sb.Length - Capacity);
        }
    }

    public void Reset() { lock (_lock) _sb.Clear(); }

    public string Snapshot() { lock (_lock) return _sb.ToString(); }
}
