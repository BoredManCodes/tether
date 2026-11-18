using System.Net;
using System.Text.Json;
using Tether.Shared;

namespace Tether.Tests;

public class TetherApiTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        public string Reply = "{\"ok\":true}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Request = r;
            Body = r.Content == null ? null : await r.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply) };
        }
    }

    private static (TetherApi, Capture) Make(string reply = "{\"ok\":true}")
    {
        var cap = new Capture { Reply = reply };
        return (new TetherApi(new HttpClient(cap), new Uri("https://example.test"), () => "tok"), cap);
    }

    [Fact]
    public async Task PostScreenHit_sends_the_fields_the_server_reads_with_the_bearer_token()
    {
        var (api, cap) = Make();
        await api.PostScreenHitAsync(new ScreenHit("zebra", "chrome.exe", "Chrome", "a zebra here", 1234));
        Assert.Equal("/api/device/screen-hit", cap.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", cap.Request.Headers.Authorization!.Scheme);
        Assert.Equal("tok", cap.Request.Headers.Authorization.Parameter);
        var j = JsonDocument.Parse(cap.Body!).RootElement;
        Assert.Equal("zebra", j.GetProperty("words").GetString());
        Assert.Equal("chrome.exe", j.GetProperty("package").GetString());
        Assert.Equal("Chrome", j.GetProperty("app_name").GetString());
        Assert.Equal("a zebra here", j.GetProperty("snippet").GetString());
        Assert.Equal(1234, j.GetProperty("ts").GetInt64());
    }

    [Fact]
    public async Task GetKeywords_splits_plain_and_regex()
    {
        var (api, _) = Make("[{\"word\":\"sex\",\"regex\":false},{\"word\":\"s[3e]x\",\"regex\":true}]");
        var (plain, regex) = await api.GetKeywordsAsync();
        Assert.Equal(new[] { "sex" }, plain);
        Assert.Equal(new[] { "s[3e]x" }, regex);
    }

    [Fact]
    public async Task GetAppRules_accepts_the_servers_0_1_integers_and_lowercases_the_package()
    {
        var (api, _) = Make("[{\"package\":\"Game.EXE\",\"blocked\":1,\"uninstall\":0,\"daily_limit_min\":null},{\"package\":\"b.exe\",\"blocked\":0,\"uninstall\":0,\"daily_limit_min\":45}]");
        var rules = await api.GetAppRulesAsync();
        Assert.Equal(new AppRule("game.exe", true, null), rules[0]);
        Assert.Equal(new AppRule("b.exe", false, 45), rules[1]);
    }

    [Fact]
    public async Task Enroll_posts_kind_pc_and_returns_the_token()
    {
        var (api, cap) = Make("{\"device_id\":7,\"token\":\"abc\"}");
        var r = await api.EnrollAsync("a@b.test", "pw", "LAPTOP");
        Assert.Equal(7, r.DeviceId);
        Assert.Equal("abc", r.Token);
        Assert.Equal("pc", JsonDocument.Parse(cap.Body!).RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task PostSync_uses_snake_case_field_names()
    {
        var (api, cap) = Make();
        await api.PostSyncAsync(new SyncBody { Usage = new() { new UsageRow("2026-11-18", "chrome.exe", "Chrome", 5000) } });
        var u = JsonDocument.Parse(cap.Body!).RootElement.GetProperty("usage")[0];
        Assert.Equal(5000, u.GetProperty("foreground_ms").GetInt64());
        Assert.Equal("chrome.exe", u.GetProperty("package").GetString());
    }
}
