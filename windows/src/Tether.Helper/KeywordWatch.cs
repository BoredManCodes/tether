using System.Collections.Concurrent;
using System.Windows.Automation;
using Tether.Shared;

namespace Tether.Helper;

/// <summary>
/// Windows version of KeywordWatch.kt. Sources: text read from the foreground window (UI
/// Automation, every 2 seconds) and a rolling buffer of typed characters. A hit sends a screenshot
/// frame and then the hit itself; the service uploads both.
/// </summary>
public sealed class KeywordWatch : IDisposable
{
    private readonly Action<PipeMsg> _send;
    private readonly HitThrottle _throttle = new(() => Environment.TickCount64);
    private readonly KeyboardBuffer _typed = new(() => Environment.TickCount64);
    private readonly BlockingCollection<char> _keys = new(new ConcurrentQueue<char>(), 1024);
    private readonly KeyboardHook _hook;
    private readonly System.Threading.Timer _poll;
    private readonly CancellationTokenSource _cts = new();
    private volatile KeywordMatcher _matcher = new(Array.Empty<string>(), Array.Empty<string>());
    private volatile bool _focusIsPassword;
    private IntPtr _lastWindow;
    private int _polling;

    public KeywordWatch(Action<PipeMsg> send)
    {
        _send = send;
        _hook = new KeyboardHook(c => _keys.TryAdd(c));   // never blocks the hook thread
        Task.Run(TypedLoop);
        _poll = new System.Threading.Timer(_ => PollScreen(), null, 2000, 2000);
        try
        {
            Automation.AddAutomationFocusChangedEventHandler((_, _) =>
            {
                try { _focusIsPassword = UiaTextReader.FocusIsPassword(AutomationElement.FocusedElement); }
                catch (Exception) { _focusIsPassword = false; }
            });
        }
        catch (Exception) { /* UIA unavailable: typed-text capture still works, password check is skipped */ }
    }

    /// <summary>Call from the thread that pumps messages.</summary>
    public void StartHook() => _hook.Install();

    public void UpdateKeywords(IEnumerable<string> plain, IEnumerable<string> regexes)
        => _matcher = new KeywordMatcher(plain, regexes);

    private void TypedLoop()
    {
        try
        {
            foreach (var c in _keys.GetConsumingEnumerable(_cts.Token))
            {
                var fg = Native.Foreground(out var hwnd);
                if (hwnd != _lastWindow) { _lastWindow = hwnd; _typed.Reset(); }
                if (_focusIsPassword) { _typed.Reset(); continue; }
                _typed.Push(c);
                if (fg == null) continue;
                Check(_typed.Snapshot(), fg.Value.Exe, fg.Value.AppName);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void PollScreen()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;   // previous read still running
        try
        {
            var fg = Native.Foreground(out var hwnd);
            if (fg == null) return;
            var text = fg.Value.Title + " " + UiaTextReader.Read(hwnd);
            Check(text, fg.Value.Exe, fg.Value.AppName);
        }
        catch (Exception) { }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void Check(string text, string exe, string appName)
    {
        var hit = _matcher.Find(text);
        if (hit == null || !_throttle.ShouldReport(hit.Word, exe)) return;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_throttle.ShouldShoot())
        {
            try { _send(PipeMsg.Of("shot", new FrameMsg(Convert.ToBase64String(ScreenCapture.CaptureJpeg()), ts))); }
            catch (Exception) { /* capture failed (secure desktop); still report the hit */ }
        }
        _send(PipeMsg.Of("hit", new ScreenHit(hit.Word, exe, appName, KeywordMatcher.Snippet(text, hit.Index, 400), ts)));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _poll.Dispose();
        _hook.Dispose();
    }
}
