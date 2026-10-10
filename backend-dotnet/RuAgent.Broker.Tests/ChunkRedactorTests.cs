using RuAgent.Broker.Tools.Terminal;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ChunkRedactorTests
{
    [Fact]
    public void Secret_crossing_emission_boundary_is_redacted_as_a_whole()
    {
        const string key = "synthetic-api-key-12345";
        var redactor = new ChunkRedactor([key]);
        var output = redactor.Push("before:" + key + ":after") + redactor.Flush();

        Assert.Equal("before:[redacted]:after", output);
        Assert.True(redactor.RedactedAnything);
    }

    [Fact]
    public void Every_chunk_split_produces_the_same_redacted_text()
    {
        const string key = "synthetic-api-key-12345";
        var input = "before:" + key + ":middle:" + key + ":after";
        for (var split = 1; split < input.Length; split++)
        {
            var redactor = new ChunkRedactor([key]);
            var output = redactor.Push(input[..split]) + redactor.Push(input[split..]) + redactor.Flush();
            Assert.Equal("before:[redacted]:middle:[redacted]:after", output);
        }
    }

    [Fact]
    public void Overlapping_keys_at_different_positions_redact_the_complete_union()
    {
        var redactor = new ChunkRedactor(["abc", "bcd", "cdef"]);
        Assert.Equal("[redacted]!", redactor.Push("abcdef!") + redactor.Flush());
    }

    [Fact]
    public void Overlapping_keys_at_the_same_position_redact_the_longest()
    {
        var redactor = new ChunkRedactor(["key", "key-long-value"]);
        Assert.Equal("[redacted]:tail", redactor.Push("key-long-value:tail") + redactor.Flush());
    }
}
