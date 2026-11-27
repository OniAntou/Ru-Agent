using RuAgent.Cli.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class SubagentPresenterTests
{
    [Fact]
    public void A_detached_child_is_labelled_background_and_its_result_names_apply_state()
    {
        var snapshot = new SubagentListResponse("run_root", "completed", "snap",
            [new("agent_one", "coding", "Fix the parser.", false,
                [new("run_child", 1, "completed", 3, "provider", "model", "chat_completions", null, null)],
                [new(2, "run_child", "completed", null, null, null, false, null)], Detached: true)],
            [], 0, new(0, 0, null, null, null, false));

        var lines = SubagentPresenter.Lines(snapshot).ToArray();

        Assert.Contains(lines, line => line.Contains("agent_one · coding · background · completed", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Result 2: completed · apply unapplied", StringComparison.Ordinal));
    }

    [Fact]
    public void An_attached_child_keeps_its_existing_line()
    {
        var snapshot = new SubagentListResponse("run_root", "completed", "snap",
            [new("agent_one", "review", "Check tests.", false,
                [new("run_child", 1, "completed", 3, "provider", "model", "chat_completions", null, null)],
                [new(1, "run_child", "completed", null, null, null, false, null)])],
            [], 0, new(0, 0, null, null, null, false));

        var line = Assert.Single(SubagentPresenter.Lines(snapshot), value => value.Contains("agent_one", StringComparison.Ordinal));

        Assert.Equal("  agent_one · review · completed · model", line);
    }
}
