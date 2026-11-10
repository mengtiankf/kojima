using Kojima.Core.Models;
using Xunit;

namespace Kojima.Tests;

public sealed class LyricsParserTests
{
    [Fact]
    public void ParsesTimedLinesAndSorts()
    {
        var result = LyricsParser.Parse("test", "[00:12.50]第二句\n[00:01.00]第一句\n[00:20.00]第三句");

        Assert.NotNull(result);
        Assert.True(result!.HasTimeline);
        Assert.Equal(3, result.Lines.Count);
        Assert.Equal("第一句", result.Lines[0].Text);
        Assert.Equal(1000, result.Lines[0].TimeMs);
        Assert.Equal(12500, result.Lines[1].TimeMs);
    }

    [Fact]
    public void ParsesMultipleStampsOnOneLine()
    {
        var result = LyricsParser.Parse("test", "[00:01.00][00:05.00]重复");

        Assert.NotNull(result);
        Assert.Equal(2, result.Lines.Count);
        Assert.All(result.Lines, line => Assert.Equal("重复", line.Text));
    }

    [Fact]
    public void PlainTextHasNoTimeline()
    {
        var result = LyricsParser.Parse("test", "第一行\n第二行");

        Assert.NotNull(result);
        Assert.False(result!.HasTimeline);
        Assert.Equal(2, result.Lines.Count);
    }

    [Fact]
    public void EmptyInputReturnsNull()
    {
        Assert.Null(LyricsParser.Parse("test", "   "));
        Assert.Null(LyricsParser.Parse("test", null));
    }
}
