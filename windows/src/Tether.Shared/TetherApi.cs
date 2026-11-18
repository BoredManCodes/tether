using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Tether.Shared;

/// <summary>Thin client for the Tether server /api/device/* endpoints (wire shapes from server/src/index.js).</summary>
public sealed class TetherApi
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly Func<string?> _token;

    public TetherApi(HttpClient http, Uri baseUrl, Func<string?> token)
    {
        _http = http;
        _base = baseUrl;
        _token = token;
    }

    private HttpRequestMessage Req(HttpMethod m, string path)
    {
        var r = new HttpRequestMessage(m, new Uri(_base, path));
        var t = _token();
        if (!string.IsNullOrEmpty(t)) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
        return r;
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage r, CancellationToken ct = default)
    {
        var resp = await _http.SendAsync(r, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {r.RequestUri!.AbsolutePath}", null, resp.StatusCode);
        return resp;
    }

    private async Task<T> GetJson<T>(string path, CancellationToken ct = default)
    {
        using var resp = await Send(Req(HttpMethod.Get, path), ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<T>(Json.Options, ct).ConfigureAwait(false))!;
    }

    private async Task PostJson<T>(string path, T body)
    {
        var r = Req(HttpMethod.Post, path);
        r.Content = JsonContent.Create(body, options: Json.Options);
        using var _ = await Send(r).ConfigureAwait(false);
    }

    public async Task<EnrollResult> EnrollAsync(string email, string password, string name)
    {
        var r = Req(HttpMethod.Post, "/api/device/enroll");
        r.Content = JsonContent.Create(new { email, password, name, kind = "pc" }, options: Json.Options);
        using var resp = await Send(r).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<EnrollResult>(Json.Options).ConfigureAwait(false))!;
    }

    private sealed record KeywordDto(string Word, bool Regex);
    public async Task<(List<string> Plain, List<string> Regexes)> GetKeywordsAsync()
    {
        var all = await GetJson<List<KeywordDto>>("/api/device/keywords").ConfigureAwait(false);
        return (all.Where(k => !k.Regex).Select(k => k.Word).ToList(), all.Where(k => k.Regex).Select(k => k.Word).ToList());
    }

    private sealed record AppRuleDto(string Package, bool Blocked, int? DailyLimitMin);
    public async Task<List<AppRule>> GetAppRulesAsync()
        => (await GetJson<List<AppRuleDto>>("/api/device/apprules").ConfigureAwait(false))
            .Select(r => new AppRule(r.Package.ToLowerInvariant(), r.Blocked, r.DailyLimitMin)).ToList();

    private sealed record ScheduleDto(string Days, int StartMin, int EndMin);
    public async Task<List<ScheduleWindow>> GetScheduleAsync()
        => (await GetJson<List<ScheduleDto>>("/api/device/schedule").ConfigureAwait(false))
            .Select(s => new ScheduleWindow(s.Days, s.StartMin, s.EndMin)).ToList();

    private sealed record VersionDto(string? Version);
    public async Task<string> GetAdblockVersionAsync()
        => (await GetJson<VersionDto>("/api/device/adblock").ConfigureAwait(false)).Version ?? "";

    public async Task<(string Version, string Text)> GetBlocklistAsync()
    {
        using var resp = await Send(Req(HttpMethod.Get, "/api/device/blocklist")).ConfigureAwait(false);
        var v = resp.Headers.TryGetValues("X-Blocklist-Version", out var vs) ? vs.First() : "";
        return (v, await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    public Task PostSyncAsync(SyncBody body) => PostJson("/api/device/sync", body);
    public Task PostScreenHitAsync(ScreenHit hit) => PostJson("/api/device/screen-hit", hit);

    public async Task PostScreenAsync(byte[] jpeg)
    {
        var r = Req(HttpMethod.Post, "/api/device/screen");
        r.Content = new ByteArrayContent(jpeg);
        r.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var _ = await Send(r).ConfigureAwait(false);
    }

    public async Task PostMediaAsync(byte[] jpeg, string kind, long ts)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(kind), "kind" },
            { new StringContent(ts.ToString()), "ts" },
        };
        var file = new ByteArrayContent(jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "upload.jpg");
        var r = Req(HttpMethod.Post, "/api/device/media");
        r.Content = form;
        using var _ = await Send(r).ConfigureAwait(false);
    }

    /// <summary>Long poll: the server holds the request for about 25 seconds.</summary>
    public Task<List<ServerCommand>> GetCommandsAsync(CancellationToken ct)
        => GetJson<List<ServerCommand>>("/api/device/commands", ct);

    public Task CommandDoneAsync(long id, bool ok) => PostJson($"/api/device/commands/{id}/done", new { ok });
}
