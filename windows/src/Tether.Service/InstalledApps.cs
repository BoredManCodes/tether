using System.Runtime.InteropServices;
using Microsoft.Win32;
using Tether.Shared;

namespace Tether.Service;

/// <summary>Programs the child can launch: Start Menu shortcuts (all users and each profile) plus Uninstall entries that name an exe.</summary>
public static class InstalledApps
{
    public static List<AppInfo> List()
    {
        var found = new Dictionary<string, string>();   // exe -> display name
        try { FromShortcuts(found); } catch (Exception) { }
        try { FromUninstallKeys(found); } catch (Exception) { }
        return found.Select(kv => new AppInfo(kv.Key, kv.Value)).OrderBy(a => a.AppName).ToList();
    }

    private static void FromShortcuts(Dictionary<string, string> found)
    {
        var roots = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs") };
        var users = Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") + "\\", "Users");
        if (Directory.Exists(users))
            foreach (var u in Directory.GetDirectories(users))
                roots.Add(Path.Combine(u, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"));

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            foreach (var root in roots.Where(Directory.Exists))
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    try
                    {
                        string target = shell.CreateShortcut(lnk).TargetPath;
                        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        var exe = Path.GetFileName(target).ToLowerInvariant();
                        if (exe.StartsWith("unins") || exe.StartsWith("uninstall")) continue;
                        found.TryAdd(exe, Path.GetFileNameWithoutExtension(lnk));
                    }
                    catch (Exception) { }
                }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static void FromUninstallKeys(Dictionary<string, string> found)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root == null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                var exe = ExePaths.FromDisplayIcon(k?.GetValue("DisplayIcon") as string ?? "");
                var display = k?.GetValue("DisplayName") as string;
                if (exe != null && !string.IsNullOrWhiteSpace(display)) found.TryAdd(exe, display);
            }
        }
    }
}

/// <summary>Whether the child account has a signed-in (active or disconnected) Windows session.</summary>
public static class Sessions
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buf, out int bytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo { public int SessionId; public IntPtr WinStation; public int State; }

    private const int WtsUserName = 5;

    public static bool UserLoggedIn(string user)
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var info, out var count)) return false;
        try
        {
            var size = Marshal.SizeOf<SessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var s = Marshal.PtrToStructure<SessionInfo>(info + i * size);
                if (!WTSQuerySessionInformation(IntPtr.Zero, s.SessionId, WtsUserName, out var buf, out _)) continue;
                try
                {
                    if (string.Equals(Marshal.PtrToStringUni(buf), user, StringComparison.OrdinalIgnoreCase)) return true;
                }
                finally { WTSFreeMemory(buf); }
            }
            return false;
        }
        finally { WTSFreeMemory(info); }
    }
}
