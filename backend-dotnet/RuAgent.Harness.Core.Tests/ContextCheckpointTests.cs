using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ContextCheckpointTests
{
    [Fact]
    public void Excerpts_preserve_bounded_diagnostics_in_the_omitted_middle()
    {
        var log = new string('a', 5000) + "\nsource.cs(8,2): error CS1002: ; expected\n"
            + new string('b', 6000);
        var result = ToolResultExcerpt.Project(log);
        Assert.Contains("error CS1002", result);
        Assert.Contains("selected diagnostic lines from omitted middle", result);
        Assert.Contains("original durable receipt", result);
        Assert.True(result.Length <= 7500);
        Assert.Equal(result, ToolResultExcerpt.Project(log));
    }

    [Fact]
    public void Diagnostic_selection_caps_lines_and_preserves_unicode()
    {
        var log = new string('a', 5000) + "\n" + string.Concat(Enumerable.Repeat(
            "FAILED case " + string.Concat(Enumerable.Repeat("\U0001F600", 500)) + "\n", 100))
            + new string('z', 2000);
        var result = ToolResultExcerpt.Project(log);
        Assert.True(result.Length <= 7500);
        Assert.DoesNotContain('\uFFFD', System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(result)));
        Assert.Contains("FAILED case", result);
    }

    [Fact]
    public void Diagnostic_after_a_long_prefix_is_still_visible()
    {
        var log = new string('a', 5000) + "\n" + new string('p', 350)
            + "\U0001F600 source.ts: error TS2322: incompatible value\n" + new string('b', 6000);
        var result = ToolResultExcerpt.Project(log);
        Assert.Contains("error TS2322", result);
        Assert.DoesNotContain('\uFFFD', System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(result)));
        Assert.True(result.Length <= 7500);
    }

    [Fact]
    public void Checkpoint_requires_every_section_in_order_and_rejects_truncation()
    {
        var text = string.Join("\n", ContextCheckpoint.Sections.Select(section => section + "\n- (none)"));
        Assert.Equal(text, ContextCheckpoint.Validate(text));
        Assert.Throws<ContextCheckpointException>(() => ContextCheckpoint.Validate(text.Replace("## Next Step", "## Missing")));
        Assert.Throws<ContextCheckpointException>(() => ContextCheckpoint.Validate(text + "\0"));
        Assert.Throws<ContextCheckpointException>(() => ContextCheckpoint.Validate(text + new string('x', 9000)));
    }

    [Fact]
    public void Excerpts_keep_log_errors_at_the_end_and_do_not_split_unicode()
    {
        var log = "build started\n" + string.Concat(Enumerable.Repeat("😀", 9000)) + "\nBUILD FAILED: CS1002";
        var result = ToolResultExcerpt.Project(log);
        Assert.StartsWith("build started", result);
        Assert.EndsWith("BUILD FAILED: CS1002", result);
        Assert.Contains("middle omitted", result);
        Assert.DoesNotContain('\uFFFD', System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(result)));
        Assert.True(result.Length < log.Length);
    }

    [Fact]
    public void Excerpts_leave_structured_output_and_small_content_unchanged()
    {
        var json = "{\"content\":\"" + new string('a', 20000) + "\",\"projection_next_offset\":20000}";
        Assert.Equal(json, ToolResultExcerpt.Project(json));
        Assert.Equal("small", ToolResultExcerpt.Project("small"));
    }

    [Fact]
    public void Excerpts_drop_receipt_only_content_hashes_and_keep_everything_else()
    {
        var hash = new string('a', 32) + new string('0', 32);
        var read = "{\"path\":\"a.py\",\"start_line\":1,\"content\":\"x = 'y'\\n\",\"has_more\":false,"
            + "\"next_line\":null,\"sha256\":\"" + hash + "\"}";
        var write = "{\"path\":\"a.py\",\"before_sha256\":\"missing\",\"after_sha256\":\"" + hash + "\",\"bytes\":8}";

        var projectedRead = ToolResultExcerpt.Project(read);
        var projectedWrite = ToolResultExcerpt.Project(write);

        Assert.Equal("{\"path\":\"a.py\",\"start_line\":1,\"content\":\"x = 'y'\\n\",\"has_more\":false,\"next_line\":null}", projectedRead);
        // "missing" is not a hash: it still tells the model the write created the file.
        Assert.Equal("{\"path\":\"a.py\",\"before_sha256\":\"missing\",\"bytes\":8}", projectedWrite);
        Assert.Equal(projectedRead, ToolResultExcerpt.Project(read));
    }

    [Fact]
    public void Excerpts_keep_hash_like_fields_that_are_not_content_hashes()
    {
        var json = "{\"sha256\":\"not-a-hash\",\"nested\":{\"sha256\":\"" + new string('b', 64) + "\"},\"body_sha256\":\"" + new string('c', 64) + "\"}";
        Assert.Same(json, ToolResultExcerpt.Project(json));
    }
}
