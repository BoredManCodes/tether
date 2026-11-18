using Tether.Shared;

namespace Tether.Tests;

public class KeyboardBufferTests
{
    [Fact]
    public void Keeps_only_the_last_64_characters()
    {
        long now = 0;
        var b = new KeyboardBuffer(() => now);
        foreach (var c in new string('a', 70) + "END") b.Push(c);
        Assert.Equal(64, b.Snapshot().Length);
        Assert.EndsWith("END", b.Snapshot());
    }

    [Fact]
    public void Reset_empties_the_buffer()
    {
        long now = 0;
        var b = new KeyboardBuffer(() => now);
        b.Push('x');
        b.Reset();
        Assert.Equal("", b.Snapshot());
    }

    [Fact]
    public void Backspace_removes_the_last_character()
    {
        long now = 0;
        var b = new KeyboardBuffer(() => now);
        foreach (var c in "zebrx") b.Push(c);
        b.Push('\b');
        b.Push('a');
        Assert.Equal("zebra", b.Snapshot());
    }

    [Fact]
    public void Three_seconds_of_idle_clears_the_buffer_before_the_next_key()
    {
        long now = 0;
        var b = new KeyboardBuffer(() => now);
        b.Push('a');
        now += 2_999;
        b.Push('b');
        Assert.Equal("ab", b.Snapshot());
        now += 3_000;
        b.Push('c');
        Assert.Equal("c", b.Snapshot());
    }
}
