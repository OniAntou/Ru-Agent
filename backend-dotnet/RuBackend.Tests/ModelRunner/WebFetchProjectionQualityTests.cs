using System.Text.Json;
using System.Text.Json.Nodes;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

public sealed class WebFetchProjectionQualityTests
{
    [Fact]
    public void Clipped_web_page_retains_json_and_cursor_counts_only_extracted_text()
    {
        var content = new string('\u754c', 1000) + "end";
        var source = Page(content, 32000, null);
        var output = StructuredToolOutputProjector.Project("web_fetch", null, source, 2000,
            ToolOutputProjectionUnit.Utf16Characters, (_, _) => throw new InvalidOperationException("generic clipping lost the page"));
        using var parsed = JsonDocument.Parse(output);
        var root = parsed.RootElement;
        var wrapped = root.GetProperty("content").GetString()!;
        var visible = wrapped.Split("\ncontent: ", 2)[1].Split("...[truncated]", 2)[0];
        Assert.Equal(32000 + visible.Length, root.GetProperty("next_offset").GetInt32());
        Assert.True(root.GetProperty("has_more").GetBoolean());
        Assert.False(root.TryGetProperty("projection_next_offset", out _));
        Assert.False(root.TryGetProperty("projection_has_more", out _));
        Assert.StartsWith("[untrusted_web_reference]", wrapped);
        Assert.EndsWith("[/untrusted_web_reference]", wrapped);
        Assert.True(output.Length <= 2000);
        Assert.True(visible.Length > 0 && visible.Length < content.Length);
    }

    [Fact]
    public void Complete_web_page_with_more_pages_preserves_the_receipt_cursor()
    {
        var source = Page("    Task<T>\nnext", 32000, 32016);
        var output = StructuredToolOutputProjector.Project("web_fetch", null, source, 2000,
            ToolOutputProjectionUnit.Utf16Characters, (_, _) => throw new InvalidOperationException());
        using var parsed = JsonDocument.Parse(output);
        Assert.Equal(32016, parsed.RootElement.GetProperty("next_offset").GetInt32());
        Assert.Contains("    Task<T>\nnext", parsed.RootElement.GetProperty("content").GetString());
        Assert.True(parsed.RootElement.GetProperty("has_more").GetBoolean());
        Assert.False(parsed.RootElement.TryGetProperty("projection_next_offset", out _));
        Assert.False(parsed.RootElement.TryGetProperty("projection_has_more", out _));
    }

    private static string Page(string content, int offset, int? nextOffset) => new JsonObject
    {
        ["untrusted"] = true,
        ["source"] = new JsonObject { ["title"] = "Guide", ["url"] = "https://example.com/docs" },
        ["content_type"] = "text/html",
        ["offset"] = offset,
        ["next_offset"] = nextOffset,
        ["truncated"] = nextOffset is not null,
        ["content"] = "[untrusted_web_reference]\nsource_title: Guide\nsource_url: https://example.com/docs\ncontent: "
            + content + (nextOffset is not null ? "...[truncated]" : "") + "\n[/untrusted_web_reference]",
    }.ToJsonString();
}
