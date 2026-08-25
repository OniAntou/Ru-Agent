using System.Text;
using RuAgent.Broker.Tools.Terminal;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class HeadTailOutputBufferTests
{
    [Fact]
    public void Small_output_is_kept_verbatim()
    {
        var buffer = new HeadTailOutputBuffer(headCapacity: 100, tailCapacity: 100);
        buffer.Append("hello");
        var rendered = buffer.Render();

        Assert.Equal("hello", rendered);
        Assert.Equal(0, buffer.DroppedChars);
        Assert.Equal(5, buffer.TotalChars);
    }

    [Fact]
    public void Oversized_output_keeps_head_and_tail_with_a_gap_marker()
    {
        var buffer = new HeadTailOutputBuffer(headCapacity: 10, tailCapacity: 10);
        buffer.Append("ABCDEFGHIJKLMNOPQRSTUVWX");
        var rendered = buffer.Render();

        Assert.StartsWith("ABCDEFGHIJ", rendered, StringComparison.Ordinal);
        Assert.EndsWith("UVWX", rendered, StringComparison.Ordinal);
        Assert.Contains("truncated", rendered, StringComparison.Ordinal);
        Assert.Equal(4, buffer.DroppedChars);
        Assert.Equal(24, buffer.TotalChars);
    }

    [Fact]
    public void Continued_appends_after_the_retention_cap_still_update_the_tail()
    {
        var buffer = new HeadTailOutputBuffer(headCapacity: 5, tailCapacity: 8);
        for (var i = 0; i < 10; i++)
        {
            buffer.Append($"chunk{i:00}");
        }

        var rendered = buffer.Render();

        Assert.StartsWith("chunk", rendered, StringComparison.Ordinal);
        Assert.EndsWith("chunk09", rendered, StringComparison.Ordinal);
        Assert.True(buffer.DroppedChars > 0);
    }

    [Fact]
    public void Byte_counters_reflect_utf8_sizes()
    {
        var buffer = new HeadTailOutputBuffer(headCapacity: 1000, tailCapacity: 1000);
        buffer.Append("naïve café");

        Assert.Equal("naïve café", buffer.Render());
        Assert.Equal(Encoding.UTF8.GetByteCount("naïve café"), buffer.TotalBytes);
    }
}
