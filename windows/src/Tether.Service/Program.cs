using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tether.Service;
using Tether.Shared;

// `Tether.Service.exe cleanup` is run by the uninstaller: put DNS and browser policy back.
if (args.Length > 0 && args[0] == "cleanup")
{
    AdapterDns.RestoreDhcp();
    BrowserPolicy.Remove();
    return 0;
}

var cfg = TetherConfig.Load(Environment.GetEnvironmentVariable("TETHER_CONFIG"));
if (cfg == null)
{
    Console.Error.WriteLine("Tether is not set up: no config.json. Run the installer.");
    return 1;
}

var pipe = Environment.GetEnvironmentVariable("TETHER_PIPE") ?? HelperConfig.Load()?.PipeName ?? "tether-helper";
var usagePath = Path.Combine(TetherConfig.Dir, "usage.json");

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "TetherService");
builder.Services.AddSingleton(sp => new Hub(cfg, pipe, usagePath, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddHostedService<PipeWorker>();
builder.Services.AddHostedService<DnsWorker>();
builder.Services.AddHostedService<GuardWorker>();
builder.Services.AddHostedService<SyncWorker>();
builder.Services.AddHostedService<CommandWorker>();
await builder.Build().RunAsync();
return 0;
