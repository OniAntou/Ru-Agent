using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class ModelContextSourceTests
{
    [Fact]
    public async Task Restart_keeps_failed_verification_even_when_compaction_claims_success()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, default);
        await fixture.PayloadStore.CaptureBaseContextAsync(request.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
        using var args = JsonDocument.Parse("""{"mode":"shell","script":"dotnet test","purpose":"verification"}""");
        var step = new AssistantStep(null, [new ModelToolCall("verify", "bash", "1.0.0", args.RootElement)],
            AssistantFinishReason.ToolCalls, 1);
        var proposal = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(20), HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Json, 4096, AssistantStepJsonCodec.Serialize(step));
        var proposedRun = NextRun(request.Run, 1, 2);
        var proposalCommit = await store.CommitAsync(new HarnessCommit(request.Run.RunId, 0, proposedRun,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(20), HarnessEventKind.ModelStepCompleted,
                modelStepId: new ModelStepId("step_11111111111111111111111111111111"), payloadId: proposal.PayloadId)],
            [proposal], []), default);
        Assert.Equal(HarnessCommitOutcome.Committed, proposalCommit.Outcome);
        var result = TextPayload(21, HarnessPayloadKind.ToolOutput, JsonSerializer.Serialize(
            new { exit_code = 1, verification_identity = new string('a', 64) }));
        var failedRun = NextRun(proposedRun, 2, 3);
        var resultCommit = await store.CommitAsync(new HarnessCommit(request.Run.RunId, 1, failedRun,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(21), HarnessEventKind.ToolExecutionCompleted,
                toolCallId: new ToolCallId("call_11111111111111111111111111111111"), providerCallId: "verify",
                executionId: new ExecutionId("exec_11111111111111111111111111111111"), payloadId: result.PayloadId,
                safeCode: "process_exit_nonzero", toolOutcome: ToolExecutionOutcome.Failed, sideEffectsPossible: false)], [result], []), default);
        Assert.Equal(HarnessCommitOutcome.Committed, resultCommit.Outcome);
        var original = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, default);
        var proposalSequence = original.Entries.Single(entry => entry.Kind == ModelContextEntryKind.AssistantStep).Sequence;
        var resultSequence = original.Entries.Single(entry => entry.Kind == ModelContextEntryKind.ToolResult).Sequence;
        // Hide each original entry behind a summary; a fabricated success cannot replace receipts.
        var compactedProposal = NextRun(failedRun, 3, 4);
        await CommitCompactionAsync(store, request.Run.RunId, compactedProposal, 2, 22, "proposal-summary",
            "All checks passed", $"event:{HarnessStoreTestFixture.Event(20).Value}", compactedProposal.UpdatedAt, proposalSequence);
        var compactedResult = NextRun(compactedProposal, 4, 5);
        await CommitCompactionAsync(store, request.Run.RunId, compactedResult, 3, 23, "result-summary",
            "All checks passed", $"event:{HarnessStoreTestFixture.Event(21).Value}", compactedResult.UpdatedAt, resultSequence);
        var restored = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, default);
        Assert.DoesNotContain(restored.Entries, entry => entry.Kind == ModelContextEntryKind.ToolResult);
        Assert.Equal("process_exit_nonzero", restored.VerificationEvidence.BlockingCode);
        Assert.True(FailedToolCompletionGuard.TryGetFailure(restored, out _));
        Assert.Same(restored.VerificationEvidence, ContextCheckpoint.WithDirective(restored).VerificationEvidence);
        Assert.StartsWith("canonical:", Assert.Single(restored.VerificationEvidence.Checks).Identity);
        using var alias = JsonDocument.Parse("""{"mode":"shell","script":"dotnet test","purpose":"verification","working_directory":"C:/workspace"}""");
        var retestStep = new AssistantStep(null, [new ModelToolCall("retest", "bash", "1.0.0", alias.RootElement)], AssistantFinishReason.ToolCalls, 1);
        var retest = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(24), HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Json,
            4096, AssistantStepJsonCodec.Serialize(retestStep));
        var retestRun = NextRun(compactedResult, 5, 6);
        var retestCommit = await store.CommitAsync(new HarnessCommit(request.Run.RunId, 4, retestRun,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(24), HarnessEventKind.ModelStepCompleted,
                modelStepId: new ModelStepId("step_22222222222222222222222222222222"), payloadId: retest.PayloadId)], [retest], []), default);
        Assert.Equal(HarnessCommitOutcome.Committed, retestCommit.Outcome);
        var passing = TextPayload(25, HarnessPayloadKind.ToolOutput, JsonSerializer.Serialize(
            new { exit_code = 0, verification_identity = new string('a', 64) }));
        var passingCommit = await store.CommitAsync(new HarnessCommit(request.Run.RunId, 5, NextRun(retestRun, 6, 7),
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(25), HarnessEventKind.ToolExecutionCompleted,
                toolCallId: new ToolCallId("call_22222222222222222222222222222222"), providerCallId: "retest",
                executionId: new ExecutionId("exec_22222222222222222222222222222222"), payloadId: passing.PayloadId,
                safeCode: "ok", toolOutcome: ToolExecutionOutcome.Succeeded, sideEffectsPossible: false)], [passing], []), default);
        Assert.Equal(HarnessCommitOutcome.Committed, passingCommit.Outcome);
        var afterRetest = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(request.Run.RunId, default);
        Assert.False(FailedToolCompletionGuard.TryGetFailure(afterRetest, out _));

    }
}
