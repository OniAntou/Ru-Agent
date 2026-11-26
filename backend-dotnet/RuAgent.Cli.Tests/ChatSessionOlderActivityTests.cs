using System.Collections.Immutable;
using RuAgent.Cli.TerminalGui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// A reopened long turn shows its newest steps under an "earlier steps" line, and the native
/// terminal loads the next older page when that line scrolls into view. No command exists for
/// it. These lock the join, the wording, and how the transcript finds the line on screen.
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
    public void Rows_of_a_page_without_narration_keep_a_transcript_entry()
    {
        var shown = new AgentRunActivityReplay([Row("a40", 40)],
            [new(39, "text", "Looking at the tail.", null), new(40, "activity", null, "a40")], true, 40, 2);
        var older = new AgentRunActivityReplay([Row("a10", 10), Row("a20", 20)], []);

        var merged = ChatSessionTurnReplay.MergeOlder(shown, older);

        Assert.Equal(new[] { "a10", "a20", "a40" },
            merged.Transcript.Where(entry => entry.Kind == "activity").Select(entry => entry.ActivityId));
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
    [InlineData(0, true, "↑ earlier steps · loading as you scroll up")]
    [InlineData(1, true, "↑ 1 earlier step · loading as you scroll up")]
    [InlineData(1200, true, "↑ 1,200 earlier steps · loading as you scroll up")]
    [InlineData(1200, false, "↑ 1,200 earlier steps not shown")]
    public void The_line_says_what_happens_without_naming_a_command(int older, bool loadsOnScroll, string expected)
    {
        var notice = ChatSessionTurnReplay.OlderStepsNotice(older, loadsOnScroll);

        Assert.Equal(expected, notice);
        Assert.DoesNotContain("/", notice);
    }

    [Fact]
    public void No_slash_command_is_added_for_older_steps()
    {
        Assert.DoesNotContain(Tui.ChatCommandCatalog.KnownCommands, command => command.Command == "/older");
        Assert.Empty(Tui.ChatCommandCatalog.FindMatches("/older"));
    }

    [Fact]
    public void Only_settled_turns_with_more_steps_draw_the_line_in_order()
    {
        var settled = Run("2026-11-26T00:00:01Z");
        var more = new AgentRunActivityReplay([], [], true, 40, 5);
        ImmutableArray<ChatReplayTurn> turns =
        [
            new("run_a", [], [], settled, more),
            new("run_b", [], [], settled, new AgentRunActivityReplay([], [])),
            new("run_c", [], [], Run(terminalAt: null), more),
            new("run_d", [], [], settled, more),
        ];

        Assert.Equal(new[] { 0, 3 }, ChatSessionTurnReplay.TurnsWithOlderSteps(turns));
    }

    private static GetAgentRunResponse Run(string? terminalAt) => new("run_a", "s", "ws_test", terminalAt is null ? "running" : "completed",
        null, "standard", 1, new string('a', 64), 2, 4, "2026-11-26T00:00:00Z", "2026-11-26T00:00:01Z", terminalAt, null);

    [Fact]
    public void The_transcript_names_which_marked_row_is_on_screen()
    {
        using var transcript = new TgTranscriptView { Frame = new System.Drawing.Rectangle(0, 0, 80, 4) };
        transcript.Append("  · " + ChatSessionTurnReplay.OlderStepsNotice(9, true), TranscriptStyle.Meta);
        for (var index = 0; index < 10; index++) transcript.Append($"older turn row {index}", TranscriptStyle.Prose);
        transcript.Append("  · " + ChatSessionTurnReplay.OlderStepsNotice(3, true), TranscriptStyle.Meta);
        for (var index = 0; index < 10; index++) transcript.Append($"newer turn row {index}", TranscriptStyle.Prose);

        // Following the tail: neither line is on screen.
        Assert.Null(transcript.FirstVisibleMatchOrdinal(ChatSessionTurnReplay.IsOlderStepsNotice));

        var moves = 0;
        transcript.ViewportMoved += () => moves++;
        transcript.ScrollRows(-10);
        Assert.Equal(1, transcript.FirstVisibleMatchOrdinal(ChatSessionTurnReplay.IsOlderStepsNotice));
        transcript.ScrollRows(-100);
        Assert.Equal(0, transcript.FirstVisibleMatchOrdinal(ChatSessionTurnReplay.IsOlderStepsNotice));
        Assert.Equal(2, moves);
    }
}
