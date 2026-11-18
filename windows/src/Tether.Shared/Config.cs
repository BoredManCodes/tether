using System.Text.Json;

namespace Tether.Shared;

/// <summary>Secret config. The installer restricts it to SYSTEM and Administrators so the helper never reads the token.</summary>
public sealed record TetherConfig(string ServerUrl, string Token, long DeviceId, string ChildUser, string[] OriginalDns)
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Tether");
    public static string SecretPath => Path.Combine(Dir, "config.json");
    public static string PublicPath => Path.Combine(Dir, "helper.json");

    public static TetherConfig? Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize<TetherConfig>(File.ReadAllText(path ?? SecretPath), Json.Options); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save(string? path = null)
    {
        var p = path ?? SecretPath;
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, JsonSerializer.Serialize(this, Json.Options));
    }
}

/// <summary>Non-secret settings the helper needs: which Windows user to watch and the pipe name.</summary>
public sealed record HelperConfig(string ChildUser, string PipeName = "tether-helper")
{
    public static HelperConfig? Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize<HelperConfig>(File.ReadAllText(path ?? TetherConfig.PublicPath), Json.Options); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save(string? path = null)
    {
        var p = path ?? TetherConfig.PublicPath;
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, JsonSerializer.Serialize(this, Json.Options));
    }
}
