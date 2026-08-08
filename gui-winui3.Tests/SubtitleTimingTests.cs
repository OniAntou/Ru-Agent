using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SubtitleTimingTests
{
    [Fact]
    public void Timing_SpreadsProportionalToLength()
    {
        var tokens = SubtitleTiming.TokenizeWithTiming("anh em khỏe", 900);
        Assert.Equal(3, tokens.Count);
        Assert.Equal(0, tokens[0].StartMs);
        Assert.Equal(900, tokens[^1].EndMs);
        Assert.True(tokens[0].EndMs <= tokens[1].StartMs);
    }

    [Fact]
    public void TotalDuration_IsPreserved()
    {
        var tokens = SubtitleTiming.TokenizeWithTiming("chào bạn xinh đẹp của em", 1000);
        Assert.Equal(1000, tokens[^1].EndMs);
        for (int i = 1; i < tokens.Count; i++)
            Assert.Equal(tokens[i - 1].EndMs, tokens[i].StartMs);
    }

    [Fact]
    public void EmptyText_ReturnsEmpty()
    {
        Assert.Empty(SubtitleTiming.TokenizeWithTiming("   ", 500));
    }
}