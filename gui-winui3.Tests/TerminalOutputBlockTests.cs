using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class TerminalOutputBlockTests
{
    [Fact]
    public void AppendText_accumulates_until_completed()
    {
        var block = new TerminalOutputBlock("exec_1", "Running test task");

        block.AppendText("line one\n");
        block.AppendText("line two");

        Assert.Equal("line one\nline two", block.Text);
        Assert.Equal("running", block.StateText);
        Assert.False(block.IsTruncated);
        Assert.Equal(string.Empty, block.FooterText);
    }

    [Fact]
    public void AppendText_stops_at_the_line_cap_and_marks_truncation()
    {
        var block = new TerminalOutputBlock("exec_1", "Running test task");
        for (var i = 0; i < TerminalOutputBlock.MaximumLines + 10; i++)
        {
            block.AppendText("line\n");
        }

        Assert.True(block.IsTruncated);
        Assert.Contains("truncated", block.FooterText, StringComparison.Ordinal);
        var appended = block.Text;
        block.AppendText("more\n");
        Assert.Equal(appended, block.Text);
    }

    [Fact]
    public void Complete_freezes_text_and_reports_state()
    {
        var block = new TerminalOutputBlock("exec_1", "Running test task");
        block.AppendText("output");
        block.Complete(failed: true);

        Assert.Equal("failed", block.StateText);
        block.AppendText("late");
        Assert.Equal("output", block.Text);

        var done = new TerminalOutputBlock("exec_2", "Running other task");
        done.Complete(failed: false);
        Assert.Equal("done", done.StateText);
    }

    [Fact]
    public void ResetText_drops_a_superseded_attempt_and_keeps_accepting_output()
    {
        var block = new TerminalOutputBlock("exec_1", "Running test task");
        block.AppendText("attempt one output");

        block.ResetText();

        Assert.Equal(string.Empty, block.Text);
        Assert.False(block.IsTruncated);
        block.AppendText("attempt two output");
        Assert.Equal("attempt two output", block.Text);
    }

    [Fact]
    public void ResetText_leaves_a_completed_block_alone()
    {
        var block = new TerminalOutputBlock("exec_1", "Running test task");
        block.AppendText("settled output");
        block.Complete(failed: false);

        block.ResetText();

        Assert.Equal("settled output", block.Text);
    }
}
