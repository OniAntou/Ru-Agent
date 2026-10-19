using System.Text;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Truncated_step_is_retried_without_dispatching_partial_calls(bool withTools)
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(new AssistantStep("unfinished output", withTools ? ToolStep().ToolCalls : [],
            AssistantFinishReason.Length, 16));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var gateway = new FakeToolGateway();
        var result = await Coordinator(model, gateway, store).RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Empty(gateway.Proposals);
        Assert.Empty(gateway.ExecuteAttempts);
        Assert.Empty(store.Commits.SelectMany(c => c.ConversationOutbox));
        var note = Assert.Single(store.Commits.SelectMany(c => c.Payloads),
            p => p.Kind == HarnessPayloadKind.ModelContextNote);
        Assert.Contains("model_output_truncated", Encoding.UTF8.GetString(note.Plaintext.Span));
        Assert.DoesNotContain(store.Commits.SelectMany(c => c.Payloads),
            p => p.Kind == HarnessPayloadKind.AssistantText);
    }

    [Theory]
    [InlineData(AssistantFinishReason.Length, "model_output_truncated", false)]
    [InlineData(AssistantFinishReason.Length, "model_output_truncated", true)]
    [InlineData(AssistantFinishReason.ContentFilter, "model_content_filtered", false)]
    [InlineData(AssistantFinishReason.ContentFilter, "model_content_filtered", true)]
    [InlineData(AssistantFinishReason.Error, "model_reply_error", false)]
    [InlineData(AssistantFinishReason.Error, "model_reply_error", true)]
    public async Task Unusable_finish_reason_never_completes_or_executes_calls(
        AssistantFinishReason reason, string code, bool withTools)
    {
        var source = new FixedContextSource(Enumerable.Range(0, AgentRecoveryNotes.MaximumNotesPerRun)
            .Select(i => ModelContextEntry.Text($"note-{i}", i + 1, ModelContextRole.System,
                $"{AgentRecoveryNotes.NotePrefix} existing-{i}")).ToArray());
        var model = new FakeModelStepRunner();
        model.Enqueue(new AssistantStep("unfinished output", withTools ? ToolStep().ToolCalls : [], reason, 16));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var gateway = new FakeToolGateway();
        var result = await Coordinator(model, gateway, store, contextSource: source)
            .RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Failed, result.Run.State);
        Assert.Equal(code, result.Run.TerminalErrorCode);
        Assert.Empty(gateway.Proposals);
        Assert.Empty(store.Commits.SelectMany(c => c.ConversationOutbox));
        Assert.DoesNotContain(store.Commits.SelectMany(c => c.Payloads),
            p => p.Kind == HarnessPayloadKind.ModelContextNote);
        var audit = Assert.Single(store.Commits.SelectMany(c => c.Payloads),
            p => p.Kind == HarnessPayloadKind.AssistantStep);
        Assert.Equal(HarnessPayloadAudience.InternalOnly, audit.Audience);
        Assert.Equal(reason, AssistantStepJsonCodec.Deserialize(audit.Plaintext).FinishReason);
    }

    [Fact]
    public async Task Truncation_stops_at_its_own_cap_before_the_global_cap()
    {
        var notes = Enumerable.Range(0, AgentRecoveryNotes.MaximumTruncatedNotes)
            .Select(i => ModelContextEntry.Text($"note-{i}", i + 1, ModelContextRole.System,
                AgentRecoveryNotes.BuildTruncatedNote())).ToArray();
        Assert.True(notes.Length < AgentRecoveryNotes.MaximumNotesPerRun);
        var model = new FakeModelStepRunner();
        model.Enqueue(new AssistantStep("unfinished", [], AssistantFinishReason.Length, 16));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var result = await Coordinator(model, new FakeToolGateway(), store,
                contextSource: new FixedContextSource(notes))
            .RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Failed, result.Run.State);
        Assert.Equal("model_output_truncated", result.ErrorCode);
        Assert.Empty(store.Commits.SelectMany(c => c.ConversationOutbox));
        Assert.DoesNotContain(store.Commits.SelectMany(c => c.Events),
            e => e.Kind == HarnessEventKind.RunNoteAdded);
    }

    [Fact]
    public async Task Truncations_separated_by_progress_do_not_exhaust_the_truncation_cap()
    {
        var entries = new List<ModelContextEntry>();
        for (var i = 0; i < AgentRecoveryNotes.MaximumTruncatedNotes; i++)
        {
            entries.Add(ModelContextEntry.Text($"note-{i}", 2 * i + 1, ModelContextRole.System,
                AgentRecoveryNotes.BuildTruncatedNote()));
            entries.Add(ModelContextEntry.Text($"user-{i}", 2 * i + 2, ModelContextRole.User, "continue"));
        }

        var model = new FakeModelStepRunner();
        model.Enqueue(new AssistantStep("unfinished", [], AssistantFinishReason.Length, 16));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var result = await Coordinator(model, new FakeToolGateway(), store,
                contextSource: new FixedContextSource(entries.ToArray()))
            .RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Contains(store.Commits.SelectMany(c => c.Events), e => e.Kind == HarnessEventKind.RunNoteAdded);
    }

    [Fact]
    public void Consecutive_count_stops_at_the_first_non_note_entry_and_skips_other_note_kinds()
    {
        var context = new ModelContextSnapshot(
        [
            ModelContextEntry.Text("n1", 1, ModelContextRole.System, AgentRecoveryNotes.BuildTruncatedNote()),
            ModelContextEntry.Text("u1", 2, ModelContextRole.User, "work"),
            ModelContextEntry.Text("n2", 3, ModelContextRole.System, AgentRecoveryNotes.BuildTruncatedNote()),
            ModelContextEntry.Text("n3", 4, ModelContextRole.System, AgentRecoveryNotes.BuildEmptyNote()),
            ModelContextEntry.Text("n4", 5, ModelContextRole.System, AgentRecoveryNotes.BuildTruncatedNote()),
        ], Array.Empty<byte>());

        Assert.Equal(2, AgentRecoveryNotes.ConsecutiveNoteCount(context, AgentRecoveryKind.OutputTruncated));
        Assert.Equal(1, AgentRecoveryNotes.ConsecutiveNoteCount(context, AgentRecoveryKind.EmptyModelReply));
        Assert.Equal(3, AgentRecoveryNotes.NoteCount(context, AgentRecoveryKind.OutputTruncated));
    }

    [Fact]
    public async Task Whitespace_reply_is_empty_rather_than_a_completed_turn()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep(" \r\n\t "));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var result = await Coordinator(model, new FakeToolGateway(), store)
            .RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Empty(store.Commits.SelectMany(c => c.ConversationOutbox));
        Assert.Contains(store.Commits.SelectMany(c => c.Events),
            e => e.SafeCode == "empty_model_reply_advised");
    }
}
