using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// A reopened long run shows its newest page first and reads older pages as the reader
/// scrolls up. These lock the parts that must not drift: which bubble carries the cursor,
/// how pages join, and that the reply keeps its persisted answer.
/// </summary>
public sealed class RunActivityPagingTests
{
    private static AgentActivityModel Model(string id, long sequence, IReadOnlyList<AgentTodoWire>? todos = null) => new(
        id, todos is null ? AgentActivityKind.Thinking : AgentActivityKind.Todo, AgentActivityState.Completed,
        null, null, null, null, null, null, null, 1000, "succeeded", null,
        DurableSequence: sequence, TransientSequence: null, Detail: null, Todos: todos);

    private static ApiService.RunActivityReplay Page(long? next, int older, params (string Id, long Sequence)[] rows) => new(
        rows.Select(row => Model(row.Id, row.Sequence)).ToArray(),
        rows.Select(row => new ApiService.RunTranscriptEntry(row.Sequence, "activity", null, row.Id)).ToArray(),
        next, older);

    private static SessionRunReplay Replay(ApiService.RunActivityReplay page) =>
        new("r", RunActivityPageMerger.Items(page, "r"), page.Transcript, page.NextBeforeSequence, page.OlderCount);

    [Fact]
    public void The_first_reply_part_carries_the_cursor_and_the_persisted_answer()
    {
        var newest = Replay(Page(next: 40, older: 12, ("a40", 40), ("a41", 41)));

        var rows = SessionTranscriptBuilder.Build(
            [new("user", "question", "r"), new("assistant", "The answer.", "r")], [newest]);

        var reply = rows[1];
        var paging = Assert.IsType<RunActivityPaging>(reply.OlderActivity);
        Assert.Equal("r", paging.RunId);
        Assert.Equal(40, paging.NextBeforeSequence);
        Assert.Equal(12, paging.OlderCount);
        Assert.Equal("The answer.", paging.PersistedText);
        Assert.Equal("Loading 12 earlier steps…", reply.OlderActivityLabel);
    }

    [Fact]
    public void A_run_with_nothing_older_shows_no_sentinel()
    {
        var rows = SessionTranscriptBuilder.Build(
            [new("user", "question", "r"), new("assistant", "The answer.", "r")],
            [Replay(Page(next: null, older: 0, ("a1", 1)))]);

        Assert.Null(rows[1].OlderActivity);
        Assert.False(rows[1].ShowOlderActivity);
    }

    [Fact]
    public void An_older_page_joins_in_durable_order_and_moves_the_cursor()
    {
        var shown = Replay(Page(next: 40, older: 2, ("a40", 40), ("a41", 41)));

        var merged = RunActivityPageMerger.Merge(shown, Page(next: null, older: 0, ("a10", 10), ("a20", 20)));

        Assert.Equal(new[] { "a10", "a20", "a40", "a41" }, merged.Activities.Select(item => item.ActivityId));
        Assert.Equal(new long[] { 10, 20, 40, 41 }, merged.Transcript.Select(entry => entry.Sequence));
        Assert.False(merged.HasMore);
        Assert.All(merged.Activities, item => Assert.Equal("r", item.OwnerRunId));
    }

    [Fact]
    public void A_checklist_the_newest_page_led_with_keeps_one_copy_at_its_own_place()
    {
        AgentTodoWire[] todos = [new("Older task", "pending")];
        // The newest page leads with a checklist older than its own rows (sequence 15).
        var newest = new ApiService.RunActivityReplay(
            [Model("todo", 15, todos), Model("a40", 40)],
            [new(15, "activity", null, "todo"), new(40, "activity", null, "a40")],
            NextBeforeSequence: 40, OlderCount: 3);
        var older = new ApiService.RunActivityReplay(
            [Model("a10", 10), Model("todo", 15, todos), Model("a20", 20)],
            [new(10, "activity", null, "a10"), new(15, "activity", null, "todo"), new(20, "activity", null, "a20")]);

        var merged = RunActivityPageMerger.Merge(Replay(newest), older);

        Assert.Equal(new[] { "a10", "todo", "a20", "a40" }, merged.Activities.Select(item => item.ActivityId));
        Assert.Single(merged.Transcript, entry => entry.ActivityId == "todo");
    }
}
