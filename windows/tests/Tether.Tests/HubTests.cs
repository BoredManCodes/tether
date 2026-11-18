using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using Tether.Service;
using Tether.Shared;

namespace Tether.Tests;

public class HubTests
{
    private static Hub MakeHub(string pipe)
    {
        var cfg = new TetherConfig("http://127.0.0.1:1", "tok", 1, Environment.UserName, new[] { "1.1.1.1" });
        var hub = new Hub(cfg, pipe, Path.Combine(Path.GetTempPath(), $"tether-usage-{Guid.NewGuid():N}.json"), NullLoggerFactory.Instance);
        hub.SetRules(new RuleList(new() { new AppRule("game.exe", true, null) }));
        return hub;
    }

    [Fact]
    public async Task Caches_are_pushed_after_the_helper_says_hello_and_not_before()
    {
        var name = $"tether-test-{Guid.NewGuid():N}";
        var hub = MakeHub(name);
        using var cts = new CancellationTokenSource();
        var run = hub.Pipe.RunAsync(cts.Token);

        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        using var reader = new StreamReader(client);
        var writer = new StreamWriter(client) { AutoFlush = true };

        // A push at connect time can land before the helper is reading, and the helper then never sees it.
        var early = reader.ReadLineAsync();
        Assert.NotSame(early, await Task.WhenAny(early, Task.Delay(700)));

        writer.WriteLine(new PipeMsg("hello").ToLine());
        var line = await early.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("rules", PipeMsg.FromLine(line!)!.Type);

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
