using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Tether.Helper;

internal static class Native
{
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder buf, int size, uint flags, IntPtr layout);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr proc, uint flags, StringBuilder name, ref uint size);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    public static string? ExePathOf(uint pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Lowercase exe file name and its file description, for the foreground window. Null when it cannot be read.</summary>
    public static (string Exe, string AppName, string Title)? Foreground(out IntPtr hwnd)
    {
        hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return null;
        var path = ExePathOf(pid);
        if (path == null) return null;
        var exe = Path.GetFileName(path).ToLowerInvariant();
        var title = new StringBuilder(512);
        GetWindowText(hwnd, title, title.Capacity);
        return (exe, DescribeExe(path, exe), title.ToString());
    }

    public static string DescribeExe(string path, string exe)
    {
        lock (Descriptions)
        {
            if (Descriptions.TryGetValue(path, out var d)) return d;
            string name;
            try
            {
                var fd = FileVersionInfo.GetVersionInfo(path).FileDescription;
                name = string.IsNullOrWhiteSpace(fd) ? exe : fd.Trim();
            }
            catch (Exception) { name = exe; }
            Descriptions[path] = name;
            return name;
        }
    }
}
