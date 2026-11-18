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
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var (word, re) in _rules)
        {
            try { if (re.IsMatch(text)) return word; }
            catch (RegexMatchTimeoutException) { }
        }
        return null;
    }
}
