using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// A model that reuses one deliberation across consecutive steps produces one
/// Thinking row per step, and the feed used to print the same paragraph once per
/// row. The rows stay -- their durations are the only report of where the step's
/// time went -- but a paragraph already read is not read again.
/// </summary>
public sealed class AgentActivityGrouperRepeatTests
{
    [Fact]
    public void Reconciling_unchanged_feed_does_not_reveal_and_hide_repeated_detail()
    {
        var rows = new List<AgentActivityItem> { Thinking("Same", 3), Thinking("Same", 4) };
        AgentActivityGrouper.Reconcile(rows);
        var changes = new List<string?>();
        rows[1].PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        for (int i = 0; i < 10; i++) AgentActivityGrouper.Reconcile(rows);
        Assert.Empty(changes);
    }

    [Fact]
    public void Reconciling_unchanged_collapsed_rows_does_not_toggle_visibility_or_duration()
    {
        var rows = new List<AgentActivityItem>
        {
            new(Row("same", 3, AgentActivityState.Completed) with { Kind = AgentActivityKind.Search }),
            new(Row("same", 4, AgentActivityState.Completed) with { Kind = AgentActivityKind.Search }),
        };
        AgentActivityGrouper.Reconcile(rows);
        Assert.False(rows[0].IsVisibleInFeed);
        Assert.Equal(7_000, rows[1].EffectiveDurationMs);
        var changes = new List<string?>();
        foreach (var row in rows) row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        for (int i = 0; i < 10; i++) AgentActivityGrouper.Reconcile(rows);
        Assert.Empty(changes);
    }

    [Fact]
    public void A_deliberation_repeated_across_steps_is_read_once()
    {
        const string repeated = "The user wants me to create a small web app.";
        var rows = new List<AgentActivityItem>
        {
            Thinking(repeated, 3),
            Thinking(repeated, 3),
            Thinking(repeated, 61),
        };

        AgentActivityGrouper.Reconcile(rows);

        Assert.True(rows[0].HasThinkingDetail, "the first step shows the deliberation");
        Assert.False(rows[1].HasThinkingDetail, "the second step repeats the same paragraph");
        Assert.False(rows[2].HasThinkingDetail, "and so does the third");

        // The rows themselves are untouched: three steps, three durations.
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.True(row.IsVisibleInFeed));
        Assert.Equal(61_000, rows[2].EffectiveDurationMs);
    }

    [Fact]
    public void A_different_deliberation_still_renders()
    {
        var rows = new List<AgentActivityItem>
        {
            Thinking("First plan.", 3),
            Thinking("A different plan entirely.", 4),
        };

        AgentActivityGrouper.Reconcile(rows);

        Assert.True(rows[0].HasThinkingDetail);
        Assert.True(rows[1].HasThinkingDetail);
    }

    [Fact]
    public void A_running_step_is_never_treated_as_a_repeat()
    {
        const string text = "Still working on it.";
        var rows = new List<AgentActivityItem>
        {
            Thinking(text, 3),
            Running(text),
        };

        AgentActivityGrouper.Reconcile(rows);

        Assert.True(rows[1].HasThinkingDetail, "a step still running shows its own text");
    }

    [Fact]
    public void The_repeat_flag_is_derived_state_not_a_sticky_one()
    {
        const string repeated = "Same words.";
        var rows = new List<AgentActivityItem>
        {
            Thinking(repeated, 3),
            Thinking(repeated, 3),
            Thinking("Different words now.", 3),
        };

        AgentActivityGrouper.Reconcile(rows);
        Assert.False(rows[1].HasThinkingDetail);

        // A later pass over a different sequence must not leave the old mark.
        var reordered = new List<AgentActivityItem> { rows[2], rows[1] };
        AgentActivityGrouper.Reconcile(reordered);

        Assert.True(reordered[1].HasThinkingDetail);
    }

    private static AgentActivityItem Thinking(string detail, int durationSeconds) =>
        new(Row(detail, durationSeconds, AgentActivityState.Completed));

    private static AgentActivityItem Running(string detail) =>
        new(Row(detail, null, AgentActivityState.Started));

    private static AgentActivityModel Row(
        string detail,
        int? durationSeconds,
        AgentActivityState state) =>
        new(
            $"act_{Guid.NewGuid():N}",
            AgentActivityKind.Thinking,
            state,
            FileName: null,
            LineStart: null,
            LineEnd: null,
            Symbol: null,
            Count: null,
            Added: null,
            Removed: null,
            DurationMs: durationSeconds is { } seconds ? seconds * 1000 : null,
            SafeCode: "succeeded",
            ToolDisplay: null,
            DurableSequence: null,
            TransientSequence: null,
            Detail: detail);
}
