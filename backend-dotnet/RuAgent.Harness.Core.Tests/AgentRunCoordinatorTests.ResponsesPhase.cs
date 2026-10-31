using System.Text.Json.Nodes;
using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Tests.Prompting;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    [Fact]
    public async Task Commentary_is_retained_for_replay_and_does_not_finish_the_run()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(PhasedStep("Inspecting.", "commentary"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var result = await Coordinator(model, new FakeToolGateway(), store).RunOneStepAsync(RunId(), default);
        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Empty(store.Commits.SelectMany(c => c.ConversationOutbox));
        var payload = Assert.Single(store.Commits.SelectMany(c => c.Payloads), p => p.Kind == HarnessPayloadKind.AssistantStep);
        Assert.Equal(HarnessPayloadAudience.ModelContext, payload.Audience);
        Assert.True(AssistantStepJsonCodec.Deserialize(payload.Plaintext).ResponsesMessages!.IsCommentaryOnly);
        Assert.Contains(store.Commits.SelectMany(c => c.Events), e => e.SafeCode == "model_commentary_only");
    }

    [Fact]
    public async Task Repeated_commentary_stops_at_its_own_recovery_bound()
    {
        var notes = Enumerable.Range(0, AgentRecoveryNotes.MaximumCommentaryNotes).Select(i =>
            ModelContextEntry.Text($"note-{i}", i + 1, ModelContextRole.System, AgentRecoveryNotes.BuildCommentaryNote())).ToArray();
        var model = new FakeModelStepRunner();
        model.Enqueue(PhasedStep("Inspecting.", "commentary"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var result = await Coordinator(model, new FakeToolGateway(), store, contextSource: new FixedContextSource(notes))
            .RunOneStepAsync(RunId(), default);
        Assert.Equal(HarnessRunState.Failed, result.Run.State);
        Assert.Equal("model_commentary_only", result.ErrorCode);
        Assert.Empty(store.Commits.SelectMany(c => c.ConversationOutbox));
    }

    [Fact]
    public async Task Final_answer_phase_can_finish_normally()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(PhasedStep("Done.", "final_answer"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var result = await Coordinator(model, new FakeToolGateway(), store).RunOneStepAsync(RunId(), default);
        Assert.Equal(HarnessRunState.WaitingHistory, result.Run.State);
        Assert.Single(store.Commits.SelectMany(c => c.ConversationOutbox));
    }

    [Theory]
    [InlineData("web_http_404")]
    [InlineData("web_network_error")]
    public async Task Final_answer_can_use_readable_web_source_when_optional_fetch_failed(string failureCode)
    {
        var contextEntries = PartialWebEvidenceGuardTests
            .Context("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, failureCode, false, true, true)
            .Entries
            .ToArray();
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("The readable source answers the question."));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store,
            contextSource: new FixedContextSource(contextEntries));

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingHistory, result.Run.State);
        Assert.Single(store.Commits.SelectMany(commit => commit.ConversationOutbox));
        Assert.Equal(ToolExecutionOutcome.Failed, contextEntries[^1].ToolReceipt!.Outcome);
        Assert.Equal(failureCode, contextEntries[^1].ToolReceipt!.Code);
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Payloads),
            payload => payload.Kind == HarnessPayloadKind.ModelContextNote);
    }

    [Fact]
    public async Task Passed_check_is_not_reopened_by_a_workspace_change_during_final_generation()
    {
        // Journal changes never reach verification (ADR 0039 item 3). A passed check with no later
        // structured source edit holds no note, so the final reply is accepted without recovery.
        var source = new FixedContextSource(VerificationCompletionTests.Batch(1, "provider-check", "bash",
            """{"mode":"shell","script":"dotnet test","purpose":"verification"}""",
            ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}"""));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("The tests pass."));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));

        var result = await Coordinator(model, new FakeToolGateway(), store, contextSource: source)
            .RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingHistory, result.Run.State);
        Assert.Null(result.ErrorCode);
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Payloads),
            payload => payload.Kind == HarnessPayloadKind.ModelContextNote);
    }

    [Fact]
    public async Task Run_version_change_during_final_generation_blocks_stale_final()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("The task is complete."));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var concurrentModel = new ConcurrentRunChangingModelStepRunner(model, async () =>
        {
            var current = await store.GetRunAsync(RunId(), CancellationToken.None)
                ?? throw new InvalidOperationException("run_not_found");
            var next = new AgentRun(current.RunId, current.SessionId, current.WorkspaceId, current.Status,
                current.Version + 1, current.LastSequence + 1, current.BudgetWindow, current.ModelStepsInWindow,
                current.ToolCallsInWindow, current.Preset, current.PolicySnapshotVersion,
                current.PolicySnapshotSha256, current.CreatedAt, current.UpdatedAt.AddSeconds(1),
                permissionProfileVersion: current.PermissionProfileVersion);
            var commit = await store.CommitAsync(new HarnessCommit(current.RunId, current.Version, next,
                [new HarnessEventIntent(new EventId("evt_" + new string('c', 32)),
                    HarnessEventKind.RunNoteAdded, safeCode: "concurrent_update")], [], []), CancellationToken.None);
            Assert.Equal(HarnessCommitOutcome.Committed, commit.Outcome);
        });

        var result = await Coordinator(concurrentModel, new FakeToolGateway(), store)
            .RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal("run_changed_during_generation", result.ErrorCode);
        Assert.Equal(HarnessRunState.Running, result.Run.State);
        Assert.Empty(store.Commits.SelectMany(commit => commit.ConversationOutbox));
    }

    [Fact]
    public void Commentary_and_its_recovery_note_cannot_erase_a_failed_tool_result()
    {
        var failed = ModelContextEntry.ToolResult("failed", 1,
            new ToolExecutionReceipt(new ExecutionId("exec_" + new string('a', 32)),
                new ToolCallId("call_" + new string('b', 32)), ToolExecutionOutcome.Failed, "edit_no_match", null, false,
                providerCallId: "call_1"), "no match");
        var commentary = ModelContextEntry.Assistant("commentary", 2, PhasedStep("Inspecting.", "commentary"));
        var note = ModelContextEntry.Text("note", 3, ModelContextRole.System, AgentRecoveryNotes.BuildCommentaryNote());
        var context = new ModelContextSnapshot([failed, commentary, note], ReadOnlyMemory<byte>.Empty);
        Assert.True(FailedToolCompletionGuard.TryGetFailure(context, out var code));
        Assert.Equal("edit_no_match", code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commentary_cannot_reset_identical_or_alternating_failed_tool_guard(bool alternating)
    {
        var entries = new List<ModelContextEntry>();
        var count = alternating ? 5 : 1;
        for (var index = 0; index < count; index++)
        {
            var step = ToolStep(path: index % 2 == 0 ? "a.txt" : "b.txt");
            entries.Add(ModelContextEntry.Assistant($"tool-step-{index}", entries.Count + 1, step));
            entries.Add(ModelContextEntry.ToolResult($"receipt-{index}", entries.Count + 1,
                new ToolExecutionReceipt(new ExecutionId("exec_" + index.ToString("x32")),
                    new ToolCallId("call_" + index.ToString("x32")), ToolExecutionOutcome.Failed, "edit_no_match", null, false,
                    providerCallId: step.ToolCalls[0].ProviderCallId), "no match"));
            entries.Add(ModelContextEntry.Assistant($"commentary-{index}", entries.Count + 1, PhasedStep("Inspecting.", "commentary")));
            entries.Add(ModelContextEntry.Text($"note-{index}", entries.Count + 1, ModelContextRole.System, AgentRecoveryNotes.BuildCommentaryNote()));
        }
        var proposed = ToolStep(path: alternating ? "b.txt" : "a.txt");
        var context = new ModelContextSnapshot(entries, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(RepetitionGuardVerdict.Advise, ConsecutiveToolCallGuard.Inspect(context, proposed.ToolCalls, out _));
    }

    [Theory]
    [InlineData(1, RepetitionGuardVerdict.NotRepeated)]
    [InlineData(2, RepetitionGuardVerdict.Advise)]
    public void Commentary_preserves_the_single_infrastructure_retry_allowance(int count, RepetitionGuardVerdict expected)
    {
        var entries = new List<ModelContextEntry>();
        for (var index = 0; index < count; index++)
        {
            var step = ToolStep();
            entries.Add(ModelContextEntry.Assistant($"tool-step-{index}", entries.Count + 1, step));
            entries.Add(ModelContextEntry.ToolResult($"receipt-{index}", entries.Count + 1,
                new ToolExecutionReceipt(new ExecutionId("exec_" + index.ToString("x32")),
                    new ToolCallId("call_" + index.ToString("x32")), ToolExecutionOutcome.Failed, "transport_failure", null, false,
                    providerCallId: step.ToolCalls[0].ProviderCallId), "not executed"));
            entries.Add(ModelContextEntry.Assistant($"commentary-{index}", entries.Count + 1, PhasedStep("Inspecting.", "commentary")));
            entries.Add(ModelContextEntry.Text($"note-{index}", entries.Count + 1, ModelContextRole.System, AgentRecoveryNotes.BuildCommentaryNote()));
        }
        Assert.Equal(expected, ConsecutiveToolCallGuard.Inspect(new ModelContextSnapshot(entries, ReadOnlyMemory<byte>.Empty),
            ToolStep().ToolCalls, out _));
    }

    private static AssistantStep PhasedStep(string text, string phase) => new(text, [], AssistantFinishReason.Stop, 1,
        responsesMessages: new ResponsesAssistantMessages([new JsonObject
        {
            ["type"] = "message", ["id"] = "msg_1", ["role"] = "assistant", ["phase"] = phase,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }),
        }]));

    private sealed class ConcurrentRunChangingModelStepRunner(
        IModelStepRunner inner,
        Func<Task> afterGeneration) : IModelStepRunner
    {
        public async Task<ModelStepResult> RunStepAsync(ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta, CancellationToken cancellationToken)
        {
            var result = await inner.RunStepAsync(request, publishDelta, cancellationToken).ConfigureAwait(false);
            await afterGeneration().ConfigureAwait(false);
            return result;
        }
    }
}
