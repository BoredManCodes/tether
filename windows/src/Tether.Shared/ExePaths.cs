namespace Tether.Shared;

public static class ExePaths
{
    /// <summary>
    /// Program file name from an Uninstall key's DisplayIcon value (`"C:\x\app.exe",0`). Null for
    /// icons, uninstallers and Windows Installer stubs, which are not programs the child launches.
    /// </summary>
    public static string? FromDisplayIcon(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        var comma = v.LastIndexOf(',');
        if (comma > 0 && int.TryParse(v[(comma + 1)..].Trim(), out _)) v = v[..comma];
        v = v.Trim().Trim('"');
        if (!v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (v.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase)) return null;
        var name = Path.GetFileName(v).ToLowerInvariant();
        if (name.StartsWith("unins") || name.StartsWith("uninstall")) return null;
        return name;
    }
}
