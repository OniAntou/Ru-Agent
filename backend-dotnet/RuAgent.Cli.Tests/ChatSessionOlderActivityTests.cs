using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// A reopened long turn shows its newest steps and names how many earlier ones remain;
/// /older joins the next page. These lock the join and the wording the user acts on.
/// </summary>
public sealed class ChatSessionOlderActivityTests
{
    private static AgentActivityModel Row(string id, long sequence, IReadOnlyList<AgentTodoWire>? todos = null) => new(
        id, todos is null ? AgentActivityKind.Thinking : AgentActivityKind.Todo, AgentActivityState.Completed,
        null, null, null, null, null, null, null, 1000, "succeeded", null,
        DurableSequence: sequence, TransientSequence: null, Todos: todos);

    [Fact]
    public void An_older_page_joins_in_durable_order_with_the_checklist_kept_once()
    {
        AgentTodoWire[] todos = [new("Older task", "pending")];
        var shown = new AgentRunActivityReplay(
            [Row("todo", 15, todos), Row("a40", 40)],
            [new(15, "activity", null, "todo"), new(40, "activity", null, "a40")],
            HasMore: true, NextBeforeSequence: 40, OlderCount: 3);
        var older = new AgentRunActivityReplay(
            [Row("a10", 10), Row("todo", 15, todos), Row("a20", 20)],
            [new(10, "activity", null, "a10"), new(15, "activity", null, "todo"), new(20, "activity", null, "a20")]);

        var merged = ChatSessionTurnReplay.MergeOlder(shown, older);

        Assert.Equal(new[] { "a10", "todo", "a20", "a40" }, merged.Activities.Select(row => row.ActivityId));
        Assert.Single(merged.Transcript, entry => entry.ActivityId == "todo");
        Assert.False(merged.HasMore);
        Assert.Null(merged.NextBeforeSequence);
    }

    [Fact]
    public void A_still_older_page_keeps_the_cursor_it_was_given()
    {
        var shown = new AgentRunActivityReplay([Row("a40", 40)], [], true, 40, 900);
        var older = new AgentRunActivityReplay([Row("a30", 30)], [], true, 30, 899);

        var merged = ChatSessionTurnReplay.MergeOlder(shown, older);

        Assert.True(merged.HasMore);
        Assert.Equal(30, merged.NextBeforeSequence);
        Assert.Equal(899, merged.OlderCount);
    }

    [Theory]
    [InlineData(0, "↑ earlier steps not shown · /older shows them")]
    [InlineData(1, "↑ 1 earlier step not shown · /older shows it")]
    [InlineData(1200, "↑ 1,200 earlier steps not shown · /older shows them")]
    public void The_notice_names_what_remains_and_the_command(int older, string expected) =>
        Assert.Equal(expected, ChatSessionTurnReplay.OlderStepsNotice(older));

    [Fact]
    public void Older_is_listed_and_does_not_change_session_state()
    {
        Assert.Contains(Tui.ChatCommandCatalog.KnownCommands, command => command.Command == "/older");
        Assert.Contains(Tui.ChatCommandCatalog.FindMatches("/ol"), command => command.Command == "/older");
    }
}
