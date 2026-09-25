using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Terminal panel block list: which block a streamed chunk joins, how a block is
/// labelled, and how the list stays bounded.
/// </summary>
public sealed class TerminalBlockListPolicyTests
{
    [Fact]
    public void A_chunk_joins_the_block_for_its_execution()
    {
        var ids = new[] { "exec_a", "exec_b", "exec_c" };

        Assert.Equal(1, TerminalBlockListPolicy.IndexOfExecution(ids, "exec_b"));
    }

    [Fact]
    public void A_chunk_for_an_unknown_execution_opens_a_new_block()
    {
        var ids = new[] { "exec_a" };

        Assert.Equal(-1, TerminalBlockListPolicy.IndexOfExecution(ids, "exec_new"));
    }

    [Fact]
    public void Concurrent_terminal_calls_do_not_share_one_block()
    {
        // Matching by execution id is what keeps two running commands apart, so their
        // output cannot interleave in one panel.
        var ids = new[] { "exec_a", "exec_b" };

        Assert.NotEqual(
            TerminalBlockListPolicy.IndexOfExecution(ids, "exec_a"),
            TerminalBlockListPolicy.IndexOfExecution(ids, "exec_b"));
    }

    [Fact]
    public void An_empty_list_has_no_match()
    {
        Assert.Equal(-1, TerminalBlockListPolicy.IndexOfExecution(Array.Empty<string>(), "exec_a"));
    }

    [Theory]
    [InlineData(null, "Command output")]
    [InlineData("", "Command output")]
    [InlineData("   ", "Command output")]
    [InlineData("dotnet test", "dotnet test")]
    [InlineData("  dotnet test  ", "dotnet test")]
    public void A_block_label_falls_back_when_the_tool_was_never_labelled(string? label, string expected)
    {
        Assert.Equal(expected, TerminalBlockListPolicy.NormalizeLabel(label));
    }

    [Fact]
    public void The_list_evicts_nothing_while_under_the_cap()
    {
        Assert.Equal(0, TerminalBlockListPolicy.EvictionCount(TerminalBlockListPolicy.MaximumBlocks - 1));
        Assert.Equal(0, TerminalBlockListPolicy.EvictionCount(TerminalBlockListPolicy.MaximumBlocks));
        Assert.False(TerminalBlockListPolicy.RequiresEviction(TerminalBlockListPolicy.MaximumBlocks - 1));
    }

    [Fact]
    public void The_list_drops_exactly_the_overflow()
    {
        int cap = TerminalBlockListPolicy.MaximumBlocks;

        Assert.Equal(1, TerminalBlockListPolicy.EvictionCount(cap + 1));
        Assert.Equal(3, TerminalBlockListPolicy.EvictionCount(cap + 3));
        Assert.True(TerminalBlockListPolicy.RequiresEviction(cap));
    }

    [Fact]
    public void Eviction_keeps_the_list_within_the_cap()
    {
        // Simulate the view's add-then-trim loop over many chunks.
        var count = 0;
        for (var index = 0; index < 40; index++)
        {
            count++;
            count -= TerminalBlockListPolicy.EvictionCount(count);
            Assert.True(
                count <= TerminalBlockListPolicy.MaximumBlocks,
                $"After {index + 1} blocks the list held {count}.");
        }

        Assert.Equal(TerminalBlockListPolicy.MaximumBlocks, count);
    }

    [Fact]
    public void A_non_positive_cap_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalBlockListPolicy.EvictionCount(3, 0));
    }

    [Fact]
    public void A_completion_settles_the_block_of_its_own_execution()
    {
        var older = new TerminalOutputBlock("exec_a", "Running first");
        var newer = new TerminalOutputBlock("exec_b", "Running second");
        var blocks = new List<TerminalOutputBlock> { older, newer };

        var target = TerminalBlockListPolicy.SelectCompletionTarget(blocks, "exec_a");

        Assert.Same(older, target);
    }

    [Fact]
    public void A_completion_never_settles_another_execution_s_block()
    {
        // The old label matching ("Running X" recorded, "Ran X" completed) fell
        // through to the newest unfinished block here, landing one command's
        // outcome on another command's output.
        var block = new TerminalOutputBlock("exec_a", "Running first");
        var blocks = new List<TerminalOutputBlock> { block };

        var target = TerminalBlockListPolicy.SelectCompletionTarget(blocks, "exec_other");

        Assert.Null(target);
        Assert.False(block.IsCompleted);
    }

    [Fact]
    public void A_completion_without_a_known_id_settles_the_newest_unfinished_block()
    {
        var older = new TerminalOutputBlock("exec_a", "Running first");
        var newer = new TerminalOutputBlock("exec_b", "Running second");
        var blocks = new List<TerminalOutputBlock> { older, newer };

        var target = TerminalBlockListPolicy.SelectCompletionTarget(blocks, null);

        Assert.Same(newer, target);
    }

    [Fact]
    public void A_completion_never_retargets_a_block_that_already_settled()
    {
        var settled = new TerminalOutputBlock("exec_a", "Ran first");
        settled.Complete(failed: false);
        var open = new TerminalOutputBlock("exec_b", "Running second");
        var blocks = new List<TerminalOutputBlock> { settled, open };

        Assert.Null(TerminalBlockListPolicy.SelectCompletionTarget(blocks, "exec_a"));
    }

    [Fact]
    public void With_no_unfinished_block_a_completion_settles_nothing()
    {
        var block = new TerminalOutputBlock("exec_a", "Ran first");
        block.Complete(failed: true);
        var blocks = new List<TerminalOutputBlock> { block };

        Assert.Null(TerminalBlockListPolicy.SelectCompletionTarget(blocks, null));
        Assert.Null(TerminalBlockListPolicy.SelectCompletionTarget(blocks, "exec_a"));
    }

    [Fact]
    public void A_block_settles_once_and_carries_the_outcome_in_its_state_text()
    {
        var block = new TerminalOutputBlock("exec_a", "Running first");
        Assert.Equal("running", block.StateText);

        block.Complete(failed: true);
        Assert.Equal("failed", block.StateText);

        block.Complete(failed: false);
        Assert.Equal("failed", block.StateText);
    }

    [Fact]
    public void A_settled_block_stops_accepting_output()
    {
        var block = new TerminalOutputBlock("exec_a", "Ran first");
        block.Complete(failed: false);

        block.AppendText("late chunk");

        Assert.Equal(string.Empty, block.Text);
    }
}
