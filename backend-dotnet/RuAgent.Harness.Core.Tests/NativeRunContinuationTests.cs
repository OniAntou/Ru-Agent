using System.Text.Json;
using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class NativeRunContinuationTests
{
    [Theory]
    [InlineData(null, SessionRunHandoff.DefaultCharacters)]
    [InlineData(32_000L, SessionRunHandoff.DefaultCharacters)]
    [InlineData(200_000L, 60_000)]
    [InlineData(1_000_000L, 300_000)]
    [InlineData(10_000_000L, SessionRunHandoff.MaximumCharacters)]
    public void The_handoff_budget_is_a_tenth_of_a_known_window_within_bounds(long? window, int expected)
    {
        Assert.Equal(expected, SessionRunHandoff.CharacterBudget(window));
    }

    [Fact]
    public void A_larger_budget_keeps_longer_excerpts()
    {
        var entry = ModelContextEntry.Text("event:long", 2, ModelContextRole.User, new string('x', 5000) + "TAIL");
        var history = new RunHandoffHistory(new RunId("run_" + new string('c', 32)), HarnessRunState.Completed,
            [ModelContextEntry.Text("event:goal", 1, ModelContextRole.User, "goal"), entry], false);

        var small = SessionRunHandoff.Render([history]);
        var large = SessionRunHandoff.Render([history], 160_000);

        Assert.DoesNotContain("TAIL", small);
        Assert.Contains("TAIL", large);
    }

    [Fact]
    public void Closed_history_is_carried_whole()
    {
        var entries = Batch(1, "call-1", withResult: true).Append(Text(4, "done")).ToArray();

        var slice = NativeRunContinuation.Close(entries);

        Assert.Equal(entries.Length, slice.Entries.Count);
        Assert.Null(slice.UnsettledNote);
        Assert.True(slice.Characters > 0);
    }

    [Fact]
    public void An_unsettled_batch_and_what_follows_it_become_a_note()
    {
        var settled = Batch(1, "call-1", withResult: true);
        var partial = new[]
        {
            Step(4, "Editing two files", "call-2", "call-3"),
            Result(5, "call-2", ToolExecutionOutcome.Failed, "execution_cancelled"),
            Text(6, "[HARNESS RECOVERY] interrupted"),
        };

        var slice = NativeRunContinuation.Close([.. settled, .. partial]);

        Assert.Equal(settled.Length, slice.Entries.Count);
        Assert.Contains("call=call-2 receipt=Failed execution_cancelled", slice.UnsettledNote);
        Assert.Contains("call=call-3 receipt=none", slice.UnsettledNote);
        Assert.Contains("Editing two files", slice.UnsettledNote);
        Assert.Contains("not proof of execution", slice.UnsettledNote);
    }

    [Fact]
    public void A_result_without_its_call_ends_the_carried_history()
    {
        var slice = NativeRunContinuation.Close([Text(1, "goal"), Result(2, "orphan", ToolExecutionOutcome.Succeeded, "ok")]);

        Assert.Single(slice.Entries);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("repeated_tool_call", true)]
    [InlineData("tool_call_budget_exhausted", true)]
    [InlineData("provider_unavailable", true)]
    [InlineData("context_limit_reached", false)]
    [InlineData("request_too_large", false)]
    [InlineData("checkpoint_failed", false)]
    public void Only_endings_that_were_not_a_context_overflow_continue_natively(string? code, bool allowed)
    {
        Assert.Equal(allowed, NativeRunContinuation.AllowsTerminalCode(code));
    }

    [Fact]
    public void Carried_history_must_leave_half_the_window_and_stay_within_the_entry_cap()
    {
        NativeRunContinuation.Slice Sized(int characters, int count = 1) =>
            new(Enumerable.Range(1, count).Select(index => Text(index, "x")).ToArray(), null, characters);

        Assert.True(NativeRunContinuation.Fits(Sized(300_000), 200_000));
        Assert.False(NativeRunContinuation.Fits(Sized(301_000), 200_000));
        // An unknown window is checked against a small assumed one, not the shared default.
        Assert.False(NativeRunContinuation.Fits(Sized(300_000), null));
        Assert.True(NativeRunContinuation.Fits(Sized(1000, NativeRunContinuation.MaximumCarriedEntries), 200_000));
        Assert.False(NativeRunContinuation.Fits(Sized(1000, NativeRunContinuation.MaximumCarriedEntries + 1), 200_000));
        Assert.False(NativeRunContinuation.Fits(Sized(0, 0), 200_000));
    }

    [Fact]
    public void Recovery_and_repetition_notes_of_the_source_run_are_not_carried()
    {
        var entries = Batch(1, "call-1", withResult: true)
            .Append(Text(4, RuAgent.Harness.Core.Prompting.ConsecutiveToolCallGuard.BuildRepairNote("edit_failed")))
            .Append(Text(5, RuAgent.Harness.Core.Prompting.AgentRecoveryNotes.NotePrefix + " try another approach"))
            .Append(Text(6, "done")).ToArray();

        var slice = NativeRunContinuation.Close(entries);

        Assert.Equal(4, slice.Entries.Count);
        Assert.DoesNotContain(slice.Entries, entry => entry.Content?.StartsWith('[') == true);
    }

    [Fact]
    public void Only_the_reserved_marker_names_a_source()
    {
        var run = new RunId("run_" + new string('d', 32));
        var encoded = NativeRunContinuation.EncodeMarker(run);

        Assert.Equal(run, NativeRunContinuation.DecodeMarker(
            ModelContextEntry.Text(NativeRunContinuation.MarkerSourceId, 1, ModelContextRole.User, encoded)));
        Assert.Null(NativeRunContinuation.DecodeMarker(
            ModelContextEntry.Text("event:quoted", 1, ModelContextRole.User, encoded)));
        Assert.Null(NativeRunContinuation.DecodeMarker(
            ModelContextEntry.Text(NativeRunContinuation.MarkerSourceId, 1, ModelContextRole.User, "not json")));
    }

    private static ModelContextEntry[] Batch(long sequence, string callId, bool withResult) => withResult
        ? [Text(sequence, "request"), Step(sequence + 1, "Checking", callId),
            Result(sequence + 2, callId, ToolExecutionOutcome.Succeeded, "ok")]
        : [Text(sequence, "request"), Step(sequence + 1, "Checking", callId)];

    private static ModelContextEntry Text(long sequence, string text) =>
        ModelContextEntry.Text("event:text-" + sequence, sequence, ModelContextRole.User, text);

    private static ModelContextEntry Step(long sequence, string text, params string[] callIds) =>
        ModelContextEntry.Assistant("event:step-" + sequence, sequence, new AssistantStep(text,
            callIds.Select(id => new ModelToolCall(id, "edit", "1", JsonDocument.Parse("{}").RootElement)).ToArray(),
            AssistantFinishReason.ToolCalls, 1));

    private static ModelContextEntry Result(long sequence, string callId, ToolExecutionOutcome outcome, string code) =>
        ModelContextEntry.ToolResult("event:result-" + sequence, sequence, new ToolExecutionReceipt(
            new ExecutionId("exec_" + sequence.ToString("x32")), new ToolCallId("call_" + sequence.ToString("x32")),
            outcome, code, null, outcome != ToolExecutionOutcome.Succeeded, providerCallId: callId), "output");
}
