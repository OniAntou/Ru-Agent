using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ContextCheckpointTests
{
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
}
