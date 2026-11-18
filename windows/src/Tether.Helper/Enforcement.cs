using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Tether.Shared;

namespace Tether.Helper;

/// <summary>Full-screen "blocked" message, shown for five seconds on the primary monitor.</summary>
internal static class BlockedOverlay
{
    public static void Show(SynchronizationContext ui, string appName)
        => ui.Post(_ =>
        {
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = Screen.PrimaryScreen!.Bounds,
                TopMost = true,
                ShowInTaskbar = false,
                BackColor = Color.Black,
            };
            form.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 28),
                TextAlign = ContentAlignment.MiddleCenter,
                Text = $"{appName}\r\nBlocked by your parent",
            });
            var timer = new System.Windows.Forms.Timer { Interval = 5000 };
            timer.Tick += (_, _) => { timer.Stop(); timer.Dispose(); form.Close(); };
            form.Shown += (_, _) => timer.Start();
            form.Show();
        }, null);
}

/// <summary>
/// Ends programs the parent's rules block, within two seconds of them starting. A standard user
/// cannot subscribe to process-start events, so this polls the child's own session.
/// </summary>
internal sealed class AppEnforcer : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<string, long> _lastShown = new();
    private volatile IReadOnlyList<AppRule> _rules = Array.Empty<AppRule>();
    private volatile IReadOnlyDictionary<string, long> _todayMs = new Dictionary<string, long>();
    private readonly int _session = Process.GetCurrentProcess().SessionId;

    public AppEnforcer(SynchronizationContext ui)
    {
        _ui = ui;
        _timer = new System.Threading.Timer(_ => Sweep(), null, 2000, 2000);
    }

    public void SetRules(IEnumerable<AppRule> rules) => _rules = rules.ToList();

    /// <summary>Today's totals as the service holds them (it adds up every usage report).</summary>
    public void SetUsageToday(IEnumerable<UsageRow> rows, string today)
        => _todayMs = rows.Where(r => r.Day == today).GroupBy(r => r.Package).ToDictionary(g => g.Key, g => g.Sum(r => r.ForegroundMs));

    private void Sweep()
    {
        var rules = _rules;
        if (rules.Count == 0) return;
        try
        {
            var byExe = new Dictionary<string, List<Process>>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.SessionId != _session) { p.Dispose(); continue; }
                    var exe = p.ProcessName.ToLowerInvariant() + ".exe";
                    if (!byExe.TryGetValue(exe, out var list)) byExe[exe] = list = new();
                    list.Add(p);
                }
                catch (Exception) { p.Dispose(); }
            }
            foreach (var exe in AppBlockPlanner.ToEnd(rules, _todayMs, byExe.Keys))
            {
                foreach (var p in byExe[exe])
                    try { p.Kill(true); } catch (Exception) { /* already gone or not ours */ }
                var now = Environment.TickCount64;
                if (!_lastShown.TryGetValue(exe, out var last) || now - last > 5000)
                {
                    _lastShown[exe] = now;
                    BlockedOverlay.Show(_ui, exe);
                }
            }
            foreach (var list in byExe.Values) foreach (var p in list) p.Dispose();
        }
        catch (Exception) { }
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>Locks the workstation while a bedtime window is active or after a LOCK command, and again after any sign-in.</summary>
internal sealed class ScheduleLocker : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly Action<bool> _onLockedChanged;
    private volatile IReadOnlyList<ScheduleWindow> _windows = Array.Empty<ScheduleWindow>();
    private volatile bool _manual;
    private bool _reported;

    public ScheduleLocker(Action<bool> onLockedChanged)
    {
        _onLockedChanged = onLockedChanged;
        _timer = new System.Threading.Timer(_ => Tick(), null, 3000, 10_000);
    }

    public void SetWindows(IEnumerable<ScheduleWindow> windows) { _windows = windows.ToList(); Tick(); }
    public void Lock() { _manual = true; Tick(); }
    public void Unlock() { _manual = false; Tick(); }

    private void Tick()
    {
        var should = LockPolicy.ShouldLock(_windows, DateTime.Now, _manual);
        if (should) Native.LockWorkStation();
        if (should != _reported) { _reported = should; _onLockedChanged(should); }
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>Samples the foreground program every 5 seconds and reports the time gained once a minute.</summary>
internal sealed class UsageSampler : IDisposable
{
    private readonly ForegroundTracker _tracker = new(() => DateTime.Now);
    private readonly System.Threading.Timer _sample, _flush;

    public UsageSampler(Action<IReadOnlyList<UsageRow>> report)
    {
        _sample = new System.Threading.Timer(_ =>
        {
            try
            {
                var fg = Native.Foreground(out IntPtr _);
                if (fg != null) _tracker.Tick(fg.Value.Exe, fg.Value.AppName, TimeSpan.FromSeconds(5));
            }
            catch (Exception) { }
        }, null, 5000, 5000);
        _flush = new System.Threading.Timer(_ =>
        {
            var rows = _tracker.Drain();
            if (rows.Count > 0) report(rows);
        }, null, 60_000, 60_000);
    }

    public void Dispose() { _sample.Dispose(); _flush.Dispose(); }
}
