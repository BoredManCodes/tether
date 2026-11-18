using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace Tether.Helper;

/// <summary>
/// Reads visible text from a window through UI Automation. Bounded on purpose: at most 500
/// elements and 200 ms per read, so a browser with hundreds of tabs cannot stall the helper.
/// Elements disappear mid-walk all the time; every failure just ends the walk with what was read.
/// </summary>
public static class UiaTextReader
{
    public const int MaxElements = 500;
    public const int BudgetMs = 200;
    private const int MaxValueChars = 2000;

    public static string Read(IntPtr hwnd)
    {
        var sb = new StringBuilder();
        var clock = Stopwatch.StartNew();
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var walker = TreeWalker.ContentViewWalker;
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(root);
            var seen = 0;
            while (queue.Count > 0 && seen < MaxElements && clock.ElapsedMilliseconds < BudgetMs)
            {
                var el = queue.Dequeue();
                seen++;
                try { AppendText(el, sb); } catch (Exception) { continue; }
                try
                {
                    for (var c = walker.GetFirstChild(el); c != null && seen + queue.Count < MaxElements * 2; c = walker.GetNextSibling(c))
                        queue.Enqueue(c);
                }
                catch (Exception) { /* element went away */ }
            }
        }
        catch (Exception) { /* secure desktop, window closed, COM error: return what we have */ }
        return sb.ToString();
    }

    private static void AppendText(AutomationElement el, StringBuilder sb)
    {
        var info = el.Current;
        if (info.IsPassword) return;
        var type = info.ControlType;
        if (type == ControlType.Text || type == ControlType.Hyperlink || type == ControlType.ListItem
            || type == ControlType.TabItem || type == ControlType.Button || type == ControlType.MenuItem)
        {
            if (!string.IsNullOrWhiteSpace(info.Name)) sb.Append(info.Name).Append(' ');
        }
        else if (type == ControlType.Edit || type == ControlType.Document)
        {
            if (!string.IsNullOrWhiteSpace(info.Name)) sb.Append(info.Name).Append(' ');
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                var v = ((ValuePattern)vp).Current.Value;
                if (!string.IsNullOrEmpty(v)) sb.Append(v.Length > MaxValueChars ? v[..MaxValueChars] : v).Append(' ');
            }
            else if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
            {
                var t = ((TextPattern)tp).DocumentRange.GetText(MaxValueChars);
                if (!string.IsNullOrEmpty(t)) sb.Append(t).Append(' ');
            }
        }
    }

    /// <summary>True when the element with keyboard focus is a password box.</summary>
    public static bool FocusIsPassword(AutomationElement? focused)
    {
        try { return focused?.Current.IsPassword ?? false; }
        catch (Exception) { return false; }
    }
}
