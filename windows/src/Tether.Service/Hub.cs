using System.Collections.Concurrent;
using Tether.Shared;

namespace Tether.Service;

/// <summary>Shared state: server link, caches pushed to the helper, DNS lookup buffer, running usage totals.</summary>
public sealed class Hub
{
    private const int MaxDnsBuffered = 5000;

    public TetherConfig Config { get; }
    public TetherApi Api { get; }
    public BlockList Blocklist { get; } = new();
    public UsageStore Usage { get; }
    public PipeHost Pipe { get; }
    public bool Locked { get; private set; }
    public volatile bool ProxyReady;
    public string BlocklistVersion { get; set; } = "";

    private readonly string _usagePath;
    private readonly ConcurrentQueue<DnsRow> _dns = new();
    private readonly ILogger _log;
    private KeywordList? _keywords;
    private RuleList? _rules;
    private ScheduleList? _schedule;

    public Hub(TetherConfig config, string pipeName, string usagePath, ILoggerFactory lf)
    {
        Config = config;
        _log = lf.CreateLogger("Hub");
        _usagePath = usagePath;
        Usage = UsageStore.Load(usagePath);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        Api = new TetherApi(http, new Uri(config.ServerUrl), () => config.Token);
        Pipe = new PipeHost(pipeName, config.ChildUser, lf.CreateLogger("Pipe"));
        Pipe.Received += OnHelperMessage;
    }

    public void RecordLookup(string name)
    {
        _dns.Enqueue(new DnsRow(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), name));
        while (_dns.Count > MaxDnsBuffered) _dns.TryDequeue(out _);
    }

    public List<DnsRow> DrainDns()
    {
        var rows = new List<DnsRow>();
        while (rows.Count < MaxDnsBuffered && _dns.TryDequeue(out var r)) rows.Add(r);
        return rows;
    }

    public void SetKeywords(KeywordList k) { _keywords = k; Pipe.Send(PipeMsg.Of("keywords", k)); }
    public void SetRules(RuleList r) { _rules = r; Pipe.Send(PipeMsg.Of("rules", r)); }
    public void SetSchedule(ScheduleList s) { _schedule = s; Pipe.Send(PipeMsg.Of("schedule", s)); }

    /// <summary>Sent each time the helper connects, and after a restart of either side.</summary>
    public void PushAll()
    {
        if (_keywords != null) Pipe.Send(PipeMsg.Of("keywords", _keywords));
        if (_rules != null) Pipe.Send(PipeMsg.Of("rules", _rules));
        if (_schedule != null) Pipe.Send(PipeMsg.Of("schedule", _schedule));
        PushUsageToday();
    }

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    private void PushUsageToday()
        => Pipe.Send(PipeMsg.Of("usage_today", new UsageList(Usage.Rows(new[] { Today }))));

    public bool SendCommandToHelper(string type)
    {
        if (!Pipe.Connected) return false;
        Pipe.Send(PipeMsg.Of("command", new CommandMsg(type)));
        return true;
    }

    private void OnHelperMessage(PipeMsg m)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                switch (m.Type)
                {
                    case "hello":
                        PushAll();   // the helper is reading now; pushing on connect can beat it
                        break;
                    case "frame":
                        await Api.PostScreenAsync(Convert.FromBase64String(m.As<FrameMsg>()!.B64)).ConfigureAwait(false);
                        break;
                    case "shot":
                        var shot = m.As<FrameMsg>()!;
                        await Api.PostMediaAsync(Convert.FromBase64String(shot.B64), "screenshot", shot.Ts).ConfigureAwait(false);
                        break;
                    case "hit":
                        await Api.PostScreenHitAsync(m.As<ScreenHit>()!).ConfigureAwait(false);
                        break;
                    case "usage":
                        Usage.Add(m.As<UsageList>()!.Rows);
                        Usage.Save(_usagePath);
                        PushUsageToday();
                        break;
                    case "locked":
                        Locked = m.As<CommandMsg>()?.Type == "locked";
                        break;
                }
            }
            catch (Exception e) { _log.LogWarning("helper message {Type} failed: {Message}", m.Type, e.Message); }
        });
    }
}
