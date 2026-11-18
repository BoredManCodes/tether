using System.Text.Json;

namespace Tether.Shared;

/// <summary>
/// One line of JSON between service and helper. Helper to service: hello, frame, shot, hit, usage.
/// Service to helper: keywords, rules, schedule, command.
/// </summary>
public sealed record PipeMsg(string Type, JsonElement? Data = null)
{
    public static PipeMsg Of<T>(string type, T data)
        => new(type, JsonSerializer.SerializeToElement(data, Json.Options));

    public T? As<T>() => Data is { } d ? d.Deserialize<T>(Json.Options) : default;

    public string ToLine() => JsonSerializer.Serialize(this, Json.Options);

    public static PipeMsg? FromLine(string line)
    {
        try { return JsonSerializer.Deserialize<PipeMsg>(line, Json.Options); }
        catch (JsonException) { return null; }
    }
}

public sealed record KeywordList(List<string> Plain, List<string> Regexes);
public sealed record RuleList(List<AppRule> Rules);
public sealed record ScheduleList(List<ScheduleWindow> Windows);
public sealed record CommandMsg(string Type);
public sealed record FrameMsg(string B64, long Ts);
public sealed record UsageList(List<UsageRow> Rows);
