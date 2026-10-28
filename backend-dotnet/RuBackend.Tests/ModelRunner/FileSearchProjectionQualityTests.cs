using System.Text.Json;
using System.Text.Json.Nodes;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

public sealed class FileSearchProjectionQualityTests
{
    [Fact]
    public void Incomplete_scan_without_a_known_next_page_does_not_invent_a_cursor()
    {
        const string source = """{"results":[],"count":0,"truncated":true,"scan_incomplete":true,"offset":0,"next_offset":null}""";
        using var arguments = JsonDocument.Parse("""{"query":"needle"}""");
        var output = StructuredToolOutputProjector.Project("grep", arguments.RootElement, source, 2000,
            ToolOutputProjectionUnit.Utf16Characters, (_, _) => throw new InvalidOperationException());
        Assert.DoesNotContain("projection_next_offset", output);
        using var parsed = JsonDocument.Parse(output);
        Assert.True(parsed.RootElement.GetProperty("scan_incomplete").GetBoolean());
    }

    [Fact]
    public void Incomplete_scan_with_null_receipt_cursor_does_not_gain_a_projection_cursor_when_clipped()
    {
        var results = new JsonArray();
        for (var index = 0; index < 100; index++)
        {
            results.Add(new JsonObject
            {
                ["path"] = $"src/File{index:D3}.cs",
                ["line"] = index + 1,
                ["column"] = 1,
                ["preview"] = new string('x', 200),
            });
        }

        var source = new JsonObject
        {
            ["results"] = results,
            ["count"] = 100,
            ["truncated"] = true,
            ["scan_incomplete"] = true,
            ["offset"] = 0,
            ["next_offset"] = null,
        }.ToJsonString();
        using var arguments = JsonDocument.Parse("""{"query":"needle"}""");
        var output = StructuredToolOutputProjector.Project("grep", arguments.RootElement, source, 2000,
            ToolOutputProjectionUnit.Utf16Characters, (_, _) => throw new InvalidOperationException());
        using var parsed = JsonDocument.Parse(output);
        var root = parsed.RootElement;

        Assert.True(root.GetProperty("scan_incomplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("next_offset").ValueKind);
        Assert.False(root.GetProperty("has_more").GetBoolean());
        Assert.Contains("Narrow the search", root.GetProperty("continuation_note").GetString());
        Assert.False(root.TryGetProperty("projection_next_offset", out _));
        Assert.False(root.TryGetProperty("projection_has_more", out _));
    }
}
