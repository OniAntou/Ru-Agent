using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// A subagent's detail view reads like the main chat: the delegated brief is the user's
/// message and the child's run replay is the reply, with the same rows and labels.
/// </summary>
public sealed class SubagentTranscriptTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_brief_is_the_users_message_and_the_replay_is_the_reply_with_every_row()
    {
        var transcript = new SubagentTranscript();
        transcript.Apply(Attempt("running", "Read Ru's offline docs on subagent limits."), new AgentRunActivityReplay(
            [Row("act_think", AgentActivityKind.Thinking, AgentActivityState.Completed),
             Row("act_docs", AgentActivityKind.Analyze, AgentActivityState.Completed, tool: "Docs")],
            [Text(1, "Searching the docs first."), Activity(2, "act_think"), Activity(3, "act_docs")]), compact: false);

        Assert.Equal(2, transcript.Messages.Count);
        var prompt = transcript.Messages[0];
        Assert.Equal("user", prompt.Role);
        Assert.Equal("Read Ru's offline docs on subagent limits.", prompt.Content);
        Assert.True(prompt.IsObserved);
        Assert.False(prompt.CanRewind);

        var reply = transcript.Messages[1];
        Assert.Same(reply, transcript.Reply);
        Assert.Equal("assistant", reply.Role);
        Assert.True(reply.IsObserved);
        Assert.True(reply.IsStreaming);
        Assert.Equal(Started.LocalDateTime, reply.Timestamp);
        var segments = reply.Segments!;
        Assert.Equal("Searching the docs first.", Assert.IsType<TextSegment>(segments[0]).Text);
        // Thinking rows are kept, as in the main chat, and tool rows keep their headline.
        Assert.Equal(AgentActivityKind.Thinking, Assert.IsType<ActivitySegment>(segments[1]).Item.Kind);
        Assert.Equal("Read the docs", Assert.IsType<ActivitySegment>(segments[2]).Item.RowVerb);
        // Thinking is shown but not counted: a step is a tool action.
        Assert.Equal(1, AgentActivitySummaryProjection.ForSteps(reply.AgentActivities!, live: true).Count);
        // Every row is done but the attempt still runs: the reply stays open, not folded.
        Assert.False(reply.AutoCollapseCompleted);
    }

    [Fact]
    public void An_attempt_without_a_brief_shows_no_prompt_bubble()
    {
        var transcript = new SubagentTranscript();
        transcript.Apply(Attempt("running", task: null), new AgentRunActivityReplay([], [Text(1, "Starting.")]), compact: false);

        Assert.Equal("assistant", Assert.Single(transcript.Messages).Role);
    }

    [Fact]
    public void A_live_read_updates_rows_in_place_and_appends_new_segments()
    {
        var transcript = new SubagentTranscript();
        transcript.Apply(Attempt("running"), new AgentRunActivityReplay(
            [Row("act_test", AgentActivityKind.Terminal, AgentActivityState.Started, detail: "python -m unittest")],
            [Text(1, "Running the tests."), Activity(2, "act_test")]), compact: false);
        var reply = transcript.Reply!;
        var segments = reply.Segments!;
        var firstText = segments[0];
        var row = Assert.IsType<ActivitySegment>(segments[1]).Item;
        Assert.True(row.IsRunning);

        transcript.Apply(Attempt("running"), new AgentRunActivityReplay(
            [Row("act_test", AgentActivityKind.Terminal, AgentActivityState.Completed, detail: "python -m unittest")],
            [Text(1, "Running the tests."), Activity(2, "act_test"), Text(3, "All green.")]), compact: false);

        Assert.Same(reply, transcript.Reply);
        Assert.Same(segments, reply.Segments);
        Assert.Same(firstText, segments[0]);
        Assert.Same(row, Assert.IsType<ActivitySegment>(segments[1]).Item);
        Assert.Same(row, Assert.Single(reply.AgentActivities!));
        Assert.True(row.IsCompleted);
        Assert.Equal("All green.", Assert.IsType<TextSegment>(segments[2]).Text);
    }

    [Fact]
    public void An_ended_attempt_settles_at_its_recorded_end_and_its_reply_carries_the_answer()
    {
        var transcript = new SubagentTranscript();
        transcript.Apply(Attempt("completed", ended: Started.AddSeconds(37)), new AgentRunActivityReplay(
            [Row("act_docs", AgentActivityKind.Analyze, AgentActivityState.Completed, tool: "Docs")],
            [Activity(1, "act_docs"), Text(2, "Subagents are capped at four.")]), compact: true);

        var reply = transcript.Reply!;
        Assert.False(reply.IsStreaming);
        Assert.Equal(TimeSpan.FromSeconds(37), reply.Duration);
        Assert.Equal("Worked for 37s", reply.ProcessHeader);
        Assert.True(transcript.HasReplyText);
        Assert.Equal("Subagents are capped at four.", reply.Content);
        // The completed result is that same reply, so it is not drawn twice.
        Assert.False(transcript.ShowsPublishedText("completed"));
    }

    [Fact]
    public void A_failed_result_is_drawn_even_after_the_child_spoke()
    {
        var transcript = new SubagentTranscript();
        transcript.Apply(Attempt("failed", ended: Started.AddSeconds(9)), new AgentRunActivityReplay(
            [], [Text(1, "Looking at the build first.")]), compact: false);

        Assert.True(transcript.HasReplyText);
        Assert.True(transcript.ShowsPublishedText("failed"));
        Assert.True(transcript.ShowsPublishedText("cancelled"));
    }

    [Fact]
    public void A_reply_without_words_leaves_the_published_result_to_the_result_view()
    {
        var transcript = new SubagentTranscript();
        transcript.Apply(Attempt("failed", ended: Started.AddSeconds(5)), new AgentRunActivityReplay(
            [Row("act_test", AgentActivityKind.Terminal, AgentActivityState.Failed, detail: "dotnet build")], []), compact: false);

        var reply = transcript.Reply!;
        Assert.False(transcript.HasReplyText);
        Assert.True(transcript.ShowsPublishedText("completed"));
        // A Host without a transcript still lists the rows in the grouped feed.
        Assert.Null(reply.Segments);
        Assert.Single(reply.AgentActivities!);
        Assert.True(reply.IsError);
    }

    [Fact]
    public void Another_attempt_starts_a_new_conversation()
    {
        var transcript = new SubagentTranscript();
        var replay = new AgentRunActivityReplay([], [Text(1, "First attempt.")]);
        transcript.Apply(Attempt("failed", ended: Started.AddSeconds(1)), replay, compact: false);
        var first = transcript.Reply;

        transcript.Apply(Attempt("running", runId: "run_child_2"), new AgentRunActivityReplay([], [Text(1, "Second.")]), compact: false);

        Assert.NotSame(first, transcript.Reply);
        Assert.Equal("run_child_2", transcript.RunId);
        Assert.Equal("Second.", transcript.Reply!.Content);
        Assert.Equal(2, transcript.Messages.Count);
    }

    [Fact]
    public void An_observed_reply_claims_no_turn_changes()
    {
        static ChatMessageItem Reply(bool observed) => new()
        {
            Role = "assistant", IsObserved = observed,
            AgentActivities = [new AgentActivityItem(
                Row("act_edit", AgentActivityKind.Edit, AgentActivityState.Completed, file: "src/a.cs"))],
        };

        Assert.NotEmpty(TurnChangesProjection.ForMessage(Reply(observed: false)).Files);
        Assert.Empty(TurnChangesProjection.ForMessage(Reply(observed: true)).Files);
        Assert.True(Reply(observed: false).CanRewind);
    }

    private static SubagentAttemptView Attempt(string status, string? task = "Investigate.",
        DateTimeOffset? ended = null, string runId = "run_child_1") => new(runId, task, status, Started, ended);

    private static AgentRunTranscriptEntry Text(long sequence, string text) => new(sequence, "text", text, null);
    private static AgentRunTranscriptEntry Activity(long sequence, string id) => new(sequence, "activity", null, id);

    private static AgentActivityModel Row(string id, AgentActivityKind kind, AgentActivityState state,
        string? detail = null, string? tool = null, string? file = null) =>
        new(id, kind, state, file, null, null, null, null, file is null ? null : 3, file is null ? null : 1, null,
            "succeeded", tool, null, null, detail);
}
