using System.Net;
using System.Net.NetworkInformation;
using Tether.Shared;

namespace Tether.Service;

internal static class Env
{
    /// <summary>Development switch: skip changing adapter DNS and browser policy.</summary>
    public static bool NoAdapter => Environment.GetEnvironmentVariable("TETHER_NO_ADAPTER") == "1";
    public static int DnsPort => int.TryParse(Environment.GetEnvironmentVariable("TETHER_DNS_PORT"), out var p) ? p : 53;
}

public sealed class PipeWorker : BackgroundService
{
    private readonly Hub _hub;
    public PipeWorker(Hub hub) => _hub = hub;
    protected override Task ExecuteAsync(CancellationToken ct) => _hub.Pipe.RunAsync(ct);
}

public sealed class DnsWorker : BackgroundService
{
    private readonly Hub _hub;
    private readonly ILogger<DnsWorker> _log;
    public DnsWorker(Hub hub, ILogger<DnsWorker> log) { _hub = hub; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var upstream = _hub.Config.OriginalDns
            .Select(s => IPAddress.TryParse(s, out var a) ? a : null)
            .Where(a => a != null && !IPAddress.IsLoopback(a)).Select(a => a!).ToList();
        while (!ct.IsCancellationRequested)
        {
            var proxy = new DnsProxy(_hub.Blocklist, upstream, _hub.RecordLookup, Env.DnsPort, _log);
            var run = proxy.RunAsync(ct);
            if (await proxy.Ready.ConfigureAwait(false))
            {
                _hub.ProxyReady = true;
                _log.LogInformation("DNS proxy listening on port {Port}", Env.DnsPort);
            }
            else _log.LogError("DNS proxy could not bind port {Port}; adapters are left alone", Env.DnsPort);
            await run.ConfigureAwait(false);
            _hub.ProxyReady = false;
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>Keeps adapter DNS and browser policy in place. Never touches adapters until the proxy is listening, so a failed bind cannot cut the internet.</summary>
public sealed class GuardWorker : BackgroundService
{
    private readonly Hub _hub;
    private readonly ILogger<GuardWorker> _log;
    private readonly SemaphoreSlim _wake = new(0);

    public GuardWorker(Hub hub, ILogger<GuardWorker> log) { _hub = hub; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (Env.NoAdapter) return;
        NetworkChange.NetworkAddressChanged += (_, _) => _wake.Release();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_hub.ProxyReady)
                {
                    BrowserPolicy.Apply();
                    AdapterDns.EnsureLoopback();
                }
            }
            catch (Exception e) { _log.LogWarning(e, "guard pass failed"); }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            await Task.Delay(2000, ct).ConfigureAwait(false);   // let a network change settle before re-checking
        }
    }
}

public sealed class SyncWorker : BackgroundService
{
    private static readonly TimeSpan FullSyncEvery = TimeSpan.FromMinutes(5);
    private readonly Hub _hub;
    private readonly ILogger<SyncWorker> _log;
    private DateTime _lastFull = DateTime.MinValue;

    public SyncWorker(Hub hub, ILogger<SyncWorker> log) { _hub = hub; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync().ConfigureAwait(false);
                if (DateTime.UtcNow - _lastFull >= FullSyncEvery)
                {
                    await _hub.Api.PostSyncAsync(BuildBody()).ConfigureAwait(false);
                    _lastFull = DateTime.UtcNow;
                }
            }
            catch (Exception e) { _log.LogWarning("sync failed: {Message}", e.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshAsync()
    {
        var (plain, regex) = await _hub.Api.GetKeywordsAsync().ConfigureAwait(false);
        _hub.SetKeywords(new KeywordList(plain, regex));
        _hub.SetRules(new RuleList(await _hub.Api.GetAppRulesAsync().ConfigureAwait(false)));
        _hub.SetSchedule(new ScheduleList(await _hub.Api.GetScheduleAsync().ConfigureAwait(false)));
        var version = await _hub.Api.GetAdblockVersionAsync().ConfigureAwait(false);
        if (version.Length > 0 && version != _hub.BlocklistVersion)
        {
            var (got, text) = await _hub.Api.GetBlocklistAsync().ConfigureAwait(false);
            _hub.Blocklist.Load(text);
            _hub.BlocklistVersion = got.Length > 0 ? got : version;
            _log.LogInformation("block list loaded: {Count} domains", _hub.Blocklist.Count);
        }
    }

    private SyncBody BuildBody()
    {
        var childOn = Sessions.UserLoggedIn(_hub.Config.ChildUser);
        var managed = !Env.NoAdapter;
        var days = new[] { DateTime.Now.ToString("yyyy-MM-dd"), DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd") };
        return new SyncBody
        {
            Status = new StatusInfo
            {
                AdminActive = true,
                // null while the child is signed out, so a normal log off is not reported as tampering
                AccessibilityActive = childOn ? _hub.Pipe.Connected : null,
                NotifListenerActive = managed ? AdapterDns.AllPointedAtProxy() && BrowserPolicy.IsIntact() : null,
                Locked = _hub.Locked,
            },
            Apps = InstalledApps.List(),
            Usage = _hub.Usage.Rows(days),
            Dns = _hub.DrainDns(),
        };
    }
}

public sealed class CommandWorker : BackgroundService
{
    private readonly Hub _hub;
    private readonly ILogger<CommandWorker> _log;
    public CommandWorker(Hub hub, ILogger<CommandWorker> log) { _hub = hub; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var c in await _hub.Api.GetCommandsAsync(ct).ConfigureAwait(false))
                {
                    var ok = c.Type switch
                    {
                        "SCREEN_START" or "SCREEN_STOP" or "SCREENSHOT" or "LOCK" or "UNLOCK" => _hub.SendCommandToHelper(c.Type),
                        _ => false,   // phone-only commands (RING, LOCATE, SCAN_MEDIA)
                    };
                    await _hub.Api.CommandDoneAsync(c.Id, ok).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _log.LogWarning("command poll failed: {Message}", e.Message);
                try { await Task.Delay(5000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
