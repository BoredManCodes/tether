using Tether.Shared;

namespace Tether.Tests;

public class BlockListTests
{
    [Fact]
    public void Listed_parent_blocks_subdomains()
    {
        var b = new BlockList();
        b.Load("example.com\nother.net\n");
        Assert.True(b.IsBlocked("a.b.example.com"));
        Assert.True(b.IsBlocked("example.com"));
    }

    [Fact]
    public void Suffix_that_is_not_a_label_boundary_is_not_blocked()
    {
        var b = new BlockList();
        b.Load("example.com");
        Assert.False(b.IsBlocked("notexample.com"));
    }

    [Fact]
    public void Lookup_is_case_insensitive_and_ignores_trailing_dot()
    {
        var b = new BlockList();
        b.Load("Example.COM\r\n");
        Assert.True(b.IsBlocked("WWW.example.com."));
    }

    [Fact]
    public void Empty_list_blocks_nothing() => Assert.False(new BlockList().IsBlocked("example.com"));
}
