using Tether.Shared;

namespace Tether.Tests;

public class KeywordSnippetTests
{
    [Fact]
    public void Find_returns_the_word_and_where_it_starts()
    {
        var hit = new KeywordMatcher(new[] { "zebra" }, Array.Empty<string>()).Find("a big ZEBRA here");
        Assert.NotNull(hit);
        Assert.Equal("zebra", hit!.Word);
        Assert.Equal(6, hit.Index);
    }

    [Fact]
    public void Snippet_is_at_most_400_chars_and_contains_the_match()
    {
        var text = new string('x', 900) + "zebra" + new string('y', 900);
        var s = KeywordMatcher.Snippet(text, 900, 400);
        Assert.True(s.Length <= 400);
        Assert.Contains("zebra", s);
    }

    [Fact]
    public void Snippet_of_short_text_is_the_whole_text_trimmed_and_collapsed()
        => Assert.Equal("a zebra b", KeywordMatcher.Snippet("  a   zebra\r\n b ", 6, 400));
}
