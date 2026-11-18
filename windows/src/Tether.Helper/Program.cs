using System.Windows.Forms;
using Tether.Shared;

namespace Tether.Helper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Only the configured child account is ever watched; any other user (the parent's admin session) exits at once.
        var cfg = HelperConfig.Load(args.Length > 1 && args[0] == "--config" ? args[1] : null);
        if (cfg == null || !string.Equals(Environment.UserName, cfg.ChildUser, StringComparison.OrdinalIgnoreCase)) return 0;

        using var single = new Mutex(true, @"Local\TetherHelper", out var first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new HelperContext(cfg));
        return 0;
    }
}

/// <summary>Owns the tray icon and every helper component; later features register here.</summary>
internal sealed class HelperContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly PipeClient _pipe;
    private readonly ScreenStreamer _screen;
    private readonly KeywordWatch _keywords;
    private readonly AppEnforcer _enforcer;
    private readonly ScheduleLocker _locker;
    private readonly UsageSampler _usage;
    private readonly System.Threading.Timer _tick;

    public HelperContext(HelperConfig cfg)
    {
        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Text = "Tether: this laptop is monitored by your parent",
            Visible = true,
        };

        _pipe = new PipeClient(cfg.PipeName);
        _screen = new ScreenStreamer(
            () => ScreenCapture.CaptureJpeg(),
            jpeg =>
            {
                _pipe.Send(PipeMsg.Of("frame", new FrameMsg(Convert.ToBase64String(jpeg), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
                return Task.CompletedTask;
            },
            () => Environment.TickCount64);

        _enforcer = new AppEnforcer(SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
        _locker = new ScheduleLocker(on => _pipe.Send(PipeMsg.Of("locked", new CommandMsg(on ? "locked" : "open"))));
        _usage = new UsageSampler(rows => _pipe.Send(PipeMsg.Of("usage", new UsageList(rows.ToList()))));
        _keywords = new KeywordWatch(_pipe.Send);
        _keywords.StartHook();
        _pipe.Received += OnMessage;
        _pipe.Start();
        _tick = new System.Threading.Timer(_ => _ = _screen.TickAsync(), null, 1000, 1000);
    }

    private void OnMessage(PipeMsg m)
    {
        if (m.Type == "keywords")
        {
            var k = m.As<KeywordList>();
            if (k != null) _keywords.UpdateKeywords(k.Plain, k.Regexes);
            return;
        }
        if (m.Type == "rules") { var r = m.As<RuleList>(); if (r != null) _enforcer.SetRules(r.Rules); return; }
        if (m.Type == "schedule") { var w = m.As<ScheduleList>(); if (w != null) _locker.SetWindows(w.Windows); return; }
        if (m.Type == "usage_today") { var u = m.As<UsageList>(); if (u != null) _enforcer.SetUsageToday(u.Rows, DateTime.Now.ToString("yyyy-MM-dd")); return; }
        if (m.Type != "command") return;
        switch (m.As<CommandMsg>()?.Type)
        {
            case "SCREEN_START": _screen.Start(); break;
            case "SCREEN_STOP": _screen.Stop(); break;
            case "SCREENSHOT": _ = _screen.CaptureOnceAsync(); break;
            case "LOCK": _locker.Lock(); break;
            case "UNLOCK": _locker.Unlock(); break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _keywords.Dispose();
            _enforcer.Dispose();
            _locker.Dispose();
            _usage.Dispose();
            _pipe.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
