using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tether.Shared;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

public sealed record EnrollResult(long DeviceId, string Token);
public sealed record ScreenHit(string Words, string Package, string AppName, string Snippet, long Ts);
public sealed record ServerCommand(long Id, string Type);
public sealed record UsageRow(string Day, string Package, string AppName, long ForegroundMs);
public sealed record AppInfo(string Package, string AppName, bool System = false);
public sealed record DnsRow(long Ts, string Domain, string? Package = null, string? AppName = null);

public sealed class StatusInfo
{
    public bool? AdminActive { get; set; }          // service running
    public bool? AccessibilityActive { get; set; }  // helper connected
    public bool? NotifListenerActive { get; set; }  // DNS and browser policy intact
    public bool Locked { get; set; }
}

public sealed class SyncBody
{
    public StatusInfo? Status { get; set; }
    public List<AppInfo>? Apps { get; set; }
    public List<UsageRow>? Usage { get; set; }
    public List<DnsRow>? Dns { get; set; }
}
