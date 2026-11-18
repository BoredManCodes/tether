namespace Tether.Shared;

/// <summary>
/// Domain list from /api/device/blocklist (one domain per line). Stored as sorted 64-bit FNV-1a
/// hashes like AdBlock.kt so a million-name list stays near 8 MB. A name is blocked when it or
/// any parent domain is listed.
/// </summary>
public sealed class BlockList
{
    private ulong[] _hashes = Array.Empty<ulong>();

    public int Count => _hashes.Length;

    public void Load(string text)
    {
        var list = new List<ulong>(1 << 16);
        foreach (var raw in text.Split('\n'))
        {
            var d = raw.Trim().ToLowerInvariant();
            if (d.Length > 0) list.Add(Fnv(d, 0));
        }
        var arr = list.ToArray();
        Array.Sort(arr);
        _hashes = arr; // single reference swap; readers see the old or new list, never a partial one
    }

    public bool IsBlocked(string name)
    {
        var set = _hashes;
        if (set.Length == 0 || string.IsNullOrEmpty(name)) return false;
        var n = name.Trim().TrimEnd('.').ToLowerInvariant();
        var start = 0;
        while (start < n.Length)
        {
            if (Array.BinarySearch(set, Fnv(n, start)) >= 0) return true;
            var dot = n.IndexOf('.', start);
            if (dot < 0) break;
            start = dot + 1;
        }
        return false;
    }

    private static ulong Fnv(string s, int from)
    {
        var h = 0xcbf29ce484222325UL;
        for (var i = from; i < s.Length; i++) h = (h ^ s[i]) * 0x100000001b3UL;
        return h;
    }
}
