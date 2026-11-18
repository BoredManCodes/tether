using Tether.Shared;

namespace Tether.Tests;

public class KeywordMatcherTests
{
    [Fact]
    public void Plain_word_does_not_match_inside_a_longer_word()
        => Assert.Null(new KeywordMatcher(new[] { "sex" }, Array.Empty<string>()).Match("a day out in Essex"));

    [Theory]
    [InlineData("sexual content")]
    [InlineData("SEX tape")]
    public void Plain_word_matches_at_a_word_start_with_open_end(string text)
        => Assert.Equal("sex", new KeywordMatcher(new[] { "sex" }, Array.Empty<string>()).Match(text));

    [Fact]
    public void Regex_entry_matches_as_given()
        => Assert.Equal("s[3e]x", new KeywordMatcher(Array.Empty<string>(), new[] { "s[3e]x" }).Match("find s3x here"));

    [Fact]
    public void Empty_lists_match_nothing()
        => Assert.Null(new KeywordMatcher(Array.Empty<string>(), Array.Empty<string>()).Match("anything at all"));

    [Fact]
    public void Invalid_regex_is_ignored_not_thrown()
        => Assert.Null(new KeywordMatcher(Array.Empty<string>(), new[] { "(" }).Match("text"));
}
