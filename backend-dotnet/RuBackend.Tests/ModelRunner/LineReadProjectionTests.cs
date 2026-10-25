using System.Text.Json;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

public sealed class LineReadProjectionTests
{
    [Fact]
    public void Model_projection_preserves_whole_lines_and_advances_line_cursor()
    {
        var content = string.Concat(Enumerable.Range(1, 100).Select(line => $"{line}: tiếng Việt 🌊\r\n"));
        var source = JsonSerializer.Serialize(new { content, path = "a.txt", start_line = 20, line_count = 100, next_line = 120, has_more = true });
        var projected = StructuredToolOutputProjector.Project("read_file", null, source, 1200,
            ToolOutputProjectionUnit.Utf16Characters, (_, _) => throw new InvalidOperationException("Unknown output format"));
        using var json = JsonDocument.Parse(projected);
        var visible = json.RootElement.GetProperty("content").GetString()!;
        Assert.EndsWith("\r\n", visible);
        var count = visible.Count(character => character == '\n');
        Assert.InRange(count, 1, 99);
        Assert.Equal(20 + count, json.RootElement.GetProperty("next_line").GetInt32());
        Assert.Equal(count, json.RootElement.GetProperty("line_count").GetInt32());
        Assert.True(json.RootElement.GetProperty("projection_has_more").GetBoolean());
    }
}
