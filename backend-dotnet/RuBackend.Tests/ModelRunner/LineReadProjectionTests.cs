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
        Assert.True(json.RootElement.GetProperty("has_more").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("projection_next_line", out _));
        Assert.False(json.RootElement.TryGetProperty("projection_has_more", out _));
    }

    [Fact]
    public void A_pdf_read_projects_without_a_byte_offset()
    {
        // The read result for a PDF is paged by `pages` and carries no `offset`; projecting
        // it used to fail the whole run as tool_output_projection_invalid.
        const string source = """{"path":"spec.pdf","kind":"pdf","format":"pdf","page_count":1,"pages":"1","content":"MARKER-PDF-4242","chars_read":15,"truncated":false}""";

        var projected = StructuredToolOutputProjector.Project("read", null, source, 1200,
            ToolOutputProjectionUnit.Utf16Characters, (_, _) => throw new InvalidOperationException("fits; no clamp expected"));

        Assert.Equal(source, projected);
    }

    [Theory]
    [InlineData("docx", "paragraph")]
    [InlineData("xlsx", "row")]
    [InlineData("pptx", "slide")]
    [InlineData("odt", "paragraph")]
    public void A_document_read_projects_without_a_byte_offset(string format, string unit)
    {
        var source = $$"""{"path":"spec.{{format}}","kind":"document","format":"{{format}}","unit":"{{unit}}","part_count":3,"pages":"1-3","content":"MARKER-DOC-77","chars_read":13,"truncated":false}""";

        var projected = StructuredToolOutputProjector.Project("read", null, source, 1200,
            ToolOutputProjectionUnit.Utf16Characters, (_, _) => throw new InvalidOperationException("fits; no clamp expected"));

        Assert.Equal(source, projected);
    }

    [Fact]
    public void A_document_read_past_the_budget_is_clamped_instead_of_failing_the_run()
    {
        var content = new string('x', 5_000);
        var source = $$"""{"path":"big.docx","kind":"document","format":"docx","unit":"paragraph","part_count":900,"pages":"1-400","content":"{{content}}","truncated":true}""";
        var clamped = false;

        var projected = StructuredToolOutputProjector.Project("read", null, source, 1200,
            ToolOutputProjectionUnit.Utf16Characters, (text, maximum) =>
            {
                clamped = true;
                return text[..maximum];
            });

        Assert.True(clamped);
        Assert.Equal(1200, projected.Length);
    }

    [Fact]
    public void Complete_visible_line_content_keeps_source_continuation_cursor()
    {
        const string source = """{"content":"first\nsecond\n","path":"a.txt","start_line":40,"line_count":2,"next_line":42}""";
        var projected = StructuredToolOutputProjector.Project("read_file", null, source, 1200,
            ToolOutputProjectionUnit.Utf16Characters, (_, _) => throw new InvalidOperationException());
        using var json = JsonDocument.Parse(projected);

        Assert.Equal(42, json.RootElement.GetProperty("next_line").GetInt32());
        Assert.True(json.RootElement.GetProperty("has_more").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("projection_next_line", out _));
        Assert.False(json.RootElement.TryGetProperty("projection_has_more", out _));
    }

    [Fact]
    public void Native_projection_preserves_raw_quotes_and_newlines_for_exact_edits()
    {
        const string sourceText = "DEFAULTS = {\"labels\": []}\r\ndef configuration():\r\n    return dict(DEFAULTS)\r\n";
        var source = JsonSerializer.Serialize(new
        {
            path = "task/config.py", content = sourceText, start_line = 1, line_count = 3,
            has_more = false, next_line = (int?)null,
        });

        var projected = StructuredToolOutputProjector.Project("read_file", null, source, 4_096,
            ToolOutputProjectionUnit.Utf16Characters, (_, _) => throw new InvalidOperationException());
        using var json = JsonDocument.Parse(projected);
        var content = json.RootElement.GetProperty("content").GetString();

        Assert.Equal(sourceText, content);
        Assert.DoesNotContain("&quot;", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("&#10;", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("1: ", content, StringComparison.Ordinal);
    }
}
