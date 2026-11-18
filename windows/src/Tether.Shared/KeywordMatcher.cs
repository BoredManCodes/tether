using System.Text.RegularExpressions;

namespace Tether.Shared;

/// <summary>
/// Same rule as KeywordWatch.kt: a plain word must start at a word boundary ("sex" does not fire
/// on "Essex") and is open at the end ("sexual" still matches). Regex entries run as given.
/// </summary>
public sealed class KeywordMatcher
{
    private readonly List<(string Word, Regex Re)> _rules = new();

    public KeywordMatcher(IEnumerable<string> plain, IEnumerable<string> regexes)
    {
        foreach (var w in plain)
        {
            if (string.IsNullOrWhiteSpace(w)) continue;
            var word = w.Trim();
            _rules.Add((word, new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(word),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))));
        }
        foreach (var r in regexes)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            try
            {
                _rules.Add((r, new Regex(r, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))));
            }
            catch (ArgumentException) { /* bad pattern from the dashboard: skip it */ }
        }
    }

    public string? Match(string text)
        => Find(text)?.Word;

    public KeywordHit? Find(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var (word, re) in _rules)
        {
            try
            {
                var m = re.Match(text);
                if (m.Success) return new KeywordHit(word, m.Index);
            }
            catch (RegexMatchTimeoutException) { }
        }
        return null;
    }

    /// <summary>Whitespace-collapsed window of at most <paramref name="max"/> chars around <paramref name="index"/>.</summary>
    public static string Snippet(string text, int index, int max)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        if (flat.Length <= max) return flat;
        // index was measured on the raw text; whitespace collapsing only shifts it left, so re-anchor on the word.
        var ratio = text.Length == 0 ? 0 : index / (double)text.Length;
        var center = (int)(flat.Length * ratio);
        var start = Math.Clamp(center - max / 2, 0, flat.Length - max);
        return flat.Substring(start, max);
    }
}

public sealed record KeywordHit(string Word, int Index);
