using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class StructuredCheckpointDurabilityTests
{
    [Fact]
    public async Task Repeated_checkpoint_restores_prior_decisions_and_auxiliary_usage_from_encrypted_history()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var ids = new CryptographicIdGenerator();
        var initial = fixture.NewRun(1, 1, 1, status: new HarnessRunStatus(HarnessRunState.Running),
            payloads: [Payload(HarnessStoreTestFixture.Payload(1), new string('a', 20000) + " Respond in Vietnamese.")]);
        await store.TryCreateRunAsync(initial, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(initial.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        await AppendAsync("Continue task.");
        var model = new Summarizer();

        for (var pass = 0; pass < 2; pass++)
        {
            // Rebuild the coordinator/source as after a Host restart, without process-local cached summaries.
            var assembler = new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore));
            var coordinator = new AgentRunCoordinator(model, new NoTools(), store, assembler, ids, new SystemTimeProvider(),
                new ToolCallScheduler(), new TransientFrameBuffer(32), contextLimit: new StaticModelContextLimitProvider(4000),
                promptContextProvider: new PromptProvider());
            var result = await coordinator.CompactAsync(initial.Run.RunId, ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);
            Assert.Equal(ContextCompactionOutcome.Committed, result.Outcome);
            Assert.Equal(pass + 1, result.Run!.ModelStepsInWindow);
            var restored = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
                .AssembleAsync(initial.Run.RunId, default);
            var summary = Assert.Single(restored.Entries, entry => entry.Kind == ModelContextEntryKind.Summary);
            Assert.Contains("Vietnamese", summary.Content);
            if (pass == 0)
            {
                await AppendAsync("New decision: preserve schema v18. " + new string('b', 20000));
                await AppendAsync("Continue the remaining work.");
            }
            else Assert.Contains("schema v18", summary.Content);
        }

        Assert.Equal(2, model.Calls);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM agent_payloads WHERE kind = $kind";
        command.Parameters.AddWithValue("$kind", (int)HarnessPayloadKind.ModelUsage);
        Assert.Equal(2L, await command.ExecuteScalarAsync());

        async Task AppendAsync(string text)
        {
            var run = (await store.GetRunAsync(initial.Run.RunId, default))!;
            var payload = Payload(ids.NewPayloadId(), text);
            var next = new AgentRun(run.RunId, run.SessionId, run.WorkspaceId, run.Status, run.Version + 1,
                run.LastSequence + 1, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow,
                run.Preset, run.PolicySnapshotVersion, run.PolicySnapshotSha256, run.CreatedAt, DateTimeOffset.UtcNow);
            var committed = await store.CommitAsync(new HarnessCommit(run.RunId, run.Version, next,
                [new HarnessEventIntent(ids.NewEventId(), HarnessEventKind.RunNoteAdded, payloadId: payload.PayloadId,
                    safeCode: "checkpoint_test_input")], [payload], []), default);
            Assert.Equal(HarnessCommitOutcome.Committed, committed.Outcome);
        }
    }

    private static HarnessPayloadIntent Payload(PayloadId id, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new HarnessPayloadIntent(id, HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.ModelContext | HarnessPayloadAudience.AuthenticatedClient,
            HarnessPayloadEncoding.Utf8Text, bytes.Length, bytes);
    }

    private sealed class Summarizer : IModelStepRunner, IContextCheckpointSummarizer
    {
        public int Calls { get; private set; }
        public Task<ContextCheckpointResult> SummarizeAsync(ModelStepRequest request, CancellationToken ct)
        {
            var history = string.Join("\n", request.Context.Entries.Select(entry => entry.Content));
            Assert.Contains("Vietnamese", history);
            if (Calls++ > 0) Assert.Contains("schema v18", history);
            var fact = Calls > 1 ? "Vietnamese responses; preserve schema v18." : "Vietnamese responses.";
            var text = string.Join("\n", ContextCheckpoint.Sections.Select(section => section + "\n- " + fact));
            return Task.FromResult(new ContextCheckpointResult(text, new ModelUsage(100, 50, 150, null, null),
                PromptReceipt: SystemPromptComposer.Compose(request.PromptContext, nativeToolCalling: true).Receipt));
        }
        public Task<ModelStepResult> RunStepAsync(ModelStepRequest request, Func<AssistantDelta, ValueTask> publish, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class PromptProvider : ISystemPromptContextProvider
    {
        public Task<SystemPromptContext> BuildAsync(AgentRun run, ModelContextSnapshot context, CancellationToken ct) => Task.FromResult(SystemPromptContext.Empty);
    }

    private sealed class NoTools : IToolGateway
    {
        public Task<ToolProposalDecision> EvaluateAsync(ToolProposal proposal, CancellationToken ct) => throw new NotSupportedException();
        public Task<PreparedToolExecution> PrepareAsync(ApprovedToolExecution execution, CancellationToken ct) => throw new NotSupportedException();
        public Task<ToolExecutionReceipt> ExecuteAsync(PreparedToolExecution execution, Func<ToolOutputChunk, ValueTask> publish, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelAsync(ExecutionId id, CancellationToken ct) => throw new NotSupportedException();
    }
}
