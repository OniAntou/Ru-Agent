using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class AgentEditPreviewCodecTests
{
    [Theory]
    [InlineData(@"C:\new\notes.txt", "actual\nnewline")]
    [InlineData("before ==> after", "  preserves whitespace  ")]
    [InlineData("", "Tiếng Việt 😀\r\n\t")]
    [InlineData("delete", "")]
    public void Round_trip_preserves_paths_delimiters_and_whitespace(string before, string after)
    {
        Assert.True(AgentEditPreviewCodec.TryDecode(AgentEditPreviewCodec.Encode(before, after), out var decodedBefore, out var decodedAfter));
        Assert.Equal(before, decodedBefore);
        Assert.Equal(after, decodedAfter);
    }

    [Fact]
    public void Large_escaped_unicode_preview_stays_bounded_and_parseable()
    {
        var content = string.Concat(Enumerable.Repeat("😀\n\\\"", 2000));
        var encoded = AgentEditPreviewCodec.Encode(content, content)!;
        Assert.True(encoded.Length <= AgentEditPreviewCodec.MaximumLength);
        Assert.True(AgentEditPreviewCodec.TryDecode(encoded, out var before, out var after));
        Assert.StartsWith(before, content);
        Assert.StartsWith(after, content);
        Assert.DoesNotContain('\uFFFD', before + after);
    }

    [Theory]
    [InlineData("@@json [\"one\"]")]
    [InlineData("@@json [null, 1]")]
    [InlineData("@@json broken")]
    public void Malformed_versioned_preview_is_not_interpreted_as_legacy(string detail) =>
        Assert.False(AgentEditPreviewCodec.TryDecode(detail, out _, out _));

    [Fact]
    public void Legacy_sessions_remain_readable_and_unsafe_controls_are_rejected()
    {
        Assert.True(AgentEditPreviewCodec.TryDecode("@@ a\\nb ==> c", out var before, out var after));
        Assert.Equal("a\nb", before);
        Assert.Equal("c", after);
        Assert.Null(AgentEditPreviewCodec.Encode("a\0b", "safe"));
    }
}
