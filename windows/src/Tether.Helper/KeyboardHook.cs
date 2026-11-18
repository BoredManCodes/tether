using System.Runtime.InteropServices;
using System.Text;

namespace Tether.Helper;

/// <summary>
/// Low-level keyboard hook (WH_KEYBOARD_LL). The callback only translates the key and hands the
/// character on; it must return quickly or Windows drops the hook. Install it on a thread that
/// pumps messages (the tray thread does).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100, WmSysKeyDown = 0x0104;
    private const int VkBack = 0x08, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkCapital = 0x14;

    private readonly Native.HookProc _proc;   // held in a field so the GC cannot collect the delegate
    private readonly Action<char> _onChar;
    private IntPtr _hook;

    public KeyboardHook(Action<char> onChar)
    {
        _onChar = onChar;
        _proc = Callback;
    }

    public void Install()
        => _hook = Native.SetWindowsHookEx(WhKeyboardLl, _proc, Native.GetModuleHandle(null), 0);

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && ((int)wParam == WmKeyDown || (int)wParam == WmSysKeyDown))
        {
            try
            {
                var vk = (uint)Marshal.ReadInt32(lParam, 0);
                var scan = (uint)Marshal.ReadInt32(lParam, 4);
                var c = Translate(vk, scan);
                if (c != '\0') _onChar(c);
            }
            catch (Exception) { /* never let an exception escape a hook */ }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static char Translate(uint vk, uint scan)
    {
        if (vk == VkBack) return '\b';
        var state = new byte[256];
        foreach (var k in new[] { VkShift, VkControl, VkMenu })
            if ((Native.GetKeyState(k) & 0x8000) != 0) state[k] = 0x80;
        if ((Native.GetKeyState(VkCapital) & 1) != 0) state[VkCapital] = 1;
        // Ctrl without Alt is a shortcut (Ctrl+C), not typing. Ctrl+Alt is AltGr and does type.
        if (state[VkControl] != 0 && state[VkMenu] == 0) return '\0';

        var sb = new StringBuilder(8);
        // Flag 4 leaves the keyboard's dead-key state alone, so the child's accents still work.
        var n = Native.ToUnicodeEx(vk, scan, state, sb, sb.Capacity, 4, Native.GetKeyboardLayout(0));
        if (n < 1) return '\0';
        var ch = sb[0];
        return ch is '\r' or '\n' or '\t' ? ' ' : ch;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
}
