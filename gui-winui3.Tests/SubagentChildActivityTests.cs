using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Views;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The subagent detail pane shows the child's own narration and tool rows while it works,
/// instead of only "Working on this task" until a result is published.
/// </summary>
public sealed class SubagentChildActivityTests
{
    [Fact]
    public void Narration_and_rows_follow_the_transcript_order_and_skip_thinking()
    {
        var replay = new AgentRunActivityReplay(
            [Row("act_think", AgentActivityKind.Thinking, AgentActivityState.Completed),
             Row("act_test", AgentActivityKind.Terminal, AgentActivityState.Started, detail: "python -m unittest")],
            [new(1, "text", "Reading the storage layer first.", null),
             new(2, "activity", null, "act_think"),
             new(3, "activity", null, "act_test")]);

        var lines = SubagentTreeView.ChildActivityLines(replay);

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].IsNarration);
        Assert.Equal("Reading the storage layer first.", lines[0].Text);
        Assert.False(lines[1].IsNarration);
        Assert.Contains("python -m unittest", lines[1].Text, StringComparison.Ordinal);
        Assert.Equal("live", lines[1].Tone);
    }

    [Fact]
    public void A_host_without_a_transcript_still_lists_rows_and_the_view_keeps_the_newest()
    {
        var rows = Enumerable.Range(0, SubagentTreeView.MaxChildActivityLines + 10)
            .Select(index => Row($"act_{index}", AgentActivityKind.Terminal, AgentActivityState.Failed, detail: $"step-{index}"))
            .ToArray();

        var lines = SubagentTreeView.ChildActivityLines(new AgentRunActivityReplay(rows, []));

        Assert.Equal(SubagentTreeView.MaxChildActivityLines, lines.Count);
        Assert.Contains($"step-{SubagentTreeView.MaxChildActivityLines + 9}", lines[^1].Text, StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Equal("danger", line.Tone));
    }

    private static AgentActivityModel Row(string id, AgentActivityKind kind, AgentActivityState state, string? detail = null) =>
        new(id, kind, state, null, null, null, null, null, null, null, null, "succeeded", null, null, null, detail);
}
