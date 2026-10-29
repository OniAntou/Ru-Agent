using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class ModelContextSourceTests
{
    [Fact]
    public async Task Explicit_replacement_survives_compaction_restart_and_external_workspace_freshness()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(90);
        var store = fixture.CreateEventStore();
        await new WorkspaceCoordinatorStore(fixture.Options).GetOrCreateAsync(workspace.WorkspaceId, default);
        var now = DateTimeOffset.UtcNow;
        var run = await CreateVerificationRunAsync(fixture, store, 90, 90, 90, includeRequest: true,
            startedAtUtc: now.AddMinutes(-3), resultCwd: "C:/workspace");
        var active = new AgentContextAssembler(
            new SqliteModelContextSource(fixture.Options, fixture.PayloadStore));
        Assert.Null((await active.AssembleAsync(run.RunId, default)).VerificationEvidence.BlockingCode);
        // This change is only in the authoritative workspace journal, not a
        // run-local file receipt. Cold replay and warm projection must agree.
        await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, null, "chg_90_cleanup", now.AddMinutes(-2));
        Assert.Equal("verification_stale", (await active.AssembleAsync(run.RunId, default)).VerificationEvidence.BlockingCode);
        var staleGuidance = (await active.AssembleAsync(run.RunId, default)).VerificationEvidence.DescribePending();
        Assert.Contains("cwd: C:/workspace", staleGuidance);

        using var args = JsonDocument.Parse("""{"executable":"python","arguments":["smoke.py"],"purpose":"verification","supersedes":["verify-freshness"]}""");
        var stepId = new ModelStepId("step_" + 904.ToString("x32"));
        var callId = new ToolCallId("call_" + 904.ToString("x32"));
        var execId = new ExecutionId("exec_" + 904.ToString("x32"));
        var proposal = new HarnessPayloadIntent(HarnessStoreTestFixture.Payload(904), HarnessPayloadKind.AssistantStep,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Json,
            4096, AssistantStepJsonCodec.Serialize(new AssistantStep(null,
                [new ModelToolCall("final-smoke", "run_process", "1.0.0", args.RootElement)], AssistantFinishReason.ToolCalls, 1)));
        var requested = NextRun(run, 4, 6);
        Assert.Equal(HarnessCommitOutcome.Committed, (await store.CommitAsync(new HarnessCommit(run.RunId, 3, requested,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(904), HarnessEventKind.ModelStepCompleted,
                modelStepId: stepId, payloadId: proposal.PayloadId),
             new HarnessEventIntent(HarnessStoreTestFixture.Event(905), HarnessEventKind.ToolExecutionRequested,
                modelStepId: stepId, toolCallId: callId, executionId: execId, providerCallId: "final-smoke")],
            [proposal], []), default)).Outcome);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE agent_events SET occurred_at_utc=$started WHERE event_id=$event";
            update.Parameters.AddWithValue("$started", now.AddMinutes(-1).ToString("O"));
            update.Parameters.AddWithValue("$event", HarnessStoreTestFixture.Event(905).Value);
            await update.ExecuteNonQueryAsync();
        }
        var output = TextPayload(906, HarnessPayloadKind.ToolOutput, JsonSerializer.Serialize(new
            { exit_code = 0, verification_identity = new string('b', 64), verification_cwd = "C:/workspace" }));
        var completed = NextRun(requested, 5, 7);
        Assert.Equal(HarnessCommitOutcome.Committed, (await store.CommitAsync(new HarnessCommit(run.RunId, 4, completed,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(906), HarnessEventKind.ToolExecutionCompleted,
                toolCallId: callId, executionId: execId, providerCallId: "final-smoke", payloadId: output.PayloadId,
                safeCode: "ok", toolOutcome: ToolExecutionOutcome.Succeeded, sideEffectsPossible: false)], [output], []), default)).Outcome);

        var passing = await active.AssembleAsync(run.RunId, default);
        Assert.Null(passing.VerificationEvidence.BlockingCode);
        var original = passing.Entries.First(entry => entry.AssistantStep is not null);
        var compacted = NextRun(completed, 6, 8);
        await CommitCompactionAsync(store, run.RunId, compacted, 5, 907, "retired-check-summary",
            "Temporary check retired; final smoke passed", original.SourceId, compacted.UpdatedAt, original.Sequence);
        var warm = await active.AssembleAsync(run.RunId, default);
        var cold = await new AgentContextAssembler(
            new SqliteModelContextSource(fixture.Options, fixture.PayloadStore, enableEntryCache: false))
            .AssembleAsync(run.RunId, default);
        Assert.Null(warm.VerificationEvidence.BlockingCode);
        Assert.Null(cold.VerificationEvidence.BlockingCode);
        Assert.Equal(2, cold.VerificationEvidence.Checks.Count);
        Assert.All(cold.VerificationEvidence.Checks, check => Assert.Equal("C:/workspace", check.WorkingDirectory));
        Assert.Single(cold.VerificationEvidence.Checks.Single(check => check.ProviderCallId == "final-smoke").SupersededCheckIdentities!);

        await AppendWorkspaceChangeAsync(fixture, workspace.WorkspaceId, null, "chg_90_later", DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.NotNull((await active.AssembleAsync(run.RunId, default)).VerificationEvidence.BlockingCode);
    }
}
