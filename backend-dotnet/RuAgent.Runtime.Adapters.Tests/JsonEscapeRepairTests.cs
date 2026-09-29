using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class JsonEscapeRepairTests
{
    private const string ValidArguments = "{\"path\":\"C:\\\\Users\\\\steal\\\\AGENTS.md\"}";

    private static string SloppyChunk(string argumentsJson) =>
        "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call-1\",\"function\":{\"name\":\"files.read\",\"arguments\":\""
        + argumentsJson.Replace("\"", "\\\"")
        + "\"}}]}}]}";

    [Fact]
    public void Complete_chunk_with_lone_backslashes_is_not_truncated_and_repairs()
    {
        var sloppy = ValidArguments.Replace(@"\\", @"\");
        var chunk = SloppyChunk(sloppy);

        Assert.False(JsonEscapeRepair.LooksTruncated(chunk));
        Assert.True(JsonEscapeRepair.TryRepairInvalidEscapes(chunk, out var repaired));
        Assert.Equal(
            "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call-1\",\"function\":{\"name\":\"files.read\",\"arguments\":\"" + ValidArguments.Replace("\"", "\\\"") + "\"}}]}}]}",
            repaired);
    }

    [Fact]
    public void Chunk_cut_mid_string_is_truncated_and_never_repaired()
    {
        var sloppy = ValidArguments.Replace(@"\\", @"\");
        var full = SloppyChunk(sloppy);
        var cut = full.IndexOf("C:\\", StringComparison.Ordinal) + 4;

        Assert.True(JsonEscapeRepair.LooksTruncated(full[..cut]));
        Assert.False(JsonEscapeRepair.TryRepairInvalidEscapes(full[..cut], out _));
    }

    [Fact]
    public void Valid_chunk_needs_no_repair()
    {
        var chunk = SloppyChunk(ValidArguments);

        Assert.False(JsonEscapeRepair.LooksTruncated(chunk));
        Assert.False(JsonEscapeRepair.TryRepairInvalidEscapes(chunk, out _));
    }
}
