using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentNativeControlTests
{
    private static async Task VerifyApplyRecoveryAsync(BackendHarnessTestFixture storage,
        InProcessExecutionFixture execution, SubagentRecord agent, string repo, PolicySnapshot policy,
        string resolutionToken, bool failFinish)
    {
        await using var connection = await storage.Options.OpenConnectionAsync(default);
        using var failpoint = connection.CreateCommand();
        failpoint.CommandText = failFinish
            ? "CREATE TRIGGER fixture_fail_apply BEFORE UPDATE OF status ON agent_subagent_apply_operations BEGIN SELECT RAISE(FAIL,'fixture_finish_failure'); END"
            : "CREATE TRIGGER fixture_fail_apply BEFORE UPDATE OF receipted ON agent_subagent_apply_files BEGIN SELECT RAISE(FAIL,'fixture_receipt_failure'); END";
        await failpoint.ExecuteNonQueryAsync();
        var journal = new SqliteSubagentApplyJournal(storage.Options, new WorkspaceJournalProtector(storage.KeyStore));
        Assert.True(ReconciliationCommand.TryParse(resolutionToken, out var command));
        var requiresInspection = command.Kind == ReconciliationCommandKind.UnknownContinue;
        var model = new ApplyRecoveryModel(agent.AgentId, requiresInspection);
        var gate = new ReconciliationInspectionGate();
        async Task<AgentRunCoordinator> Coordinator(ReconciliationInspectionGate currentGate) =>
            await new AgentRuntimeCoordinatorFactory(model, storage.EventStore, storage.Options, storage.PayloadStore,
                inspectionGate: currentGate, settings: new NativeSettings(), time: new SystemTimeProvider(),
                contextLimit: new StaticModelContextLimitProvider(100000), brokerGateways: execution.Factory)
                .CreateAsync(repo, policy, agent.RootRunId, (await storage.EventStore.GetRunAsync(agent.RootRunId, default))!.SessionId.Value);
        var coordinator = await Coordinator(gate);
        var interrupted = await coordinator.RunOneStepAsync(agent.RootRunId, default);
        Assert.Equal(HarnessRunState.ReconciliationRequired, interrupted.Run.State);
        var unknown = Assert.Single(interrupted.Receipts);
        Assert.Equal(ToolExecutionOutcome.OutcomeUnknown, unknown.Outcome);
        Assert.Equal("child", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
        var saved = (await journal.ReadAsync(agent.RootRunId.Value, agent.AgentId, 1, default))!;
        Assert.Equal(failFinish ? SubagentApplyStatus.Pending : SubagentApplyStatus.Unknown, saved.Outcome.Status);
        Assert.Equal(failFinish, Assert.Single(saved.Files).Receipted);
        var pending = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            journal.ReadAppliedBaseHashesAsync(agent.RootRunId.Value, agent.AgentId, 2, default));
        Assert.Equal("subagent_apply_reconciliation_required", pending.Code);
        failpoint.CommandText = "DROP TRIGGER fixture_fail_apply";
        await failpoint.ExecuteNonQueryAsync();
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "later parent edit");

        var beforeDecision = (await storage.EventStore.GetRunAsync(agent.RootRunId, default))!;
        var required = new List<HarnessEvent>();
        await foreach (var envelope in storage.EventStore.ReadEventsAsync(agent.RootRunId, 0, default)) required.Add(envelope.Event);
        var target = Assert.Single(required, e => e.Kind == "reconciliation.required");
        var next = new AgentRun(beforeDecision.RunId, beforeDecision.SessionId, beforeDecision.WorkspaceId,
            new(HarnessRunState.Running), beforeDecision.Version + 1, beforeDecision.LastSequence + 1,
            beforeDecision.BudgetWindow, beforeDecision.ModelStepsInWindow, beforeDecision.ToolCallsInWindow,
            beforeDecision.Preset, beforeDecision.PolicySnapshotVersion, beforeDecision.PolicySnapshotSha256,
            beforeDecision.CreatedAt, DateTimeOffset.UtcNow);
        var forged = new HarnessCommit(agent.RootRunId, beforeDecision.Version, next,
            [new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.ReconciliationResolved,
                modelStepId: target.ModelStepId, toolCallId: target.ToolCallId,
                executionId: new("exec_" + Guid.NewGuid().ToString("N")), safeCode: resolutionToken)], [], []);
        var wrongTarget = await Assert.ThrowsAsync<HarnessStoreException>(() => storage.EventStore.CommitAsync(forged, default));
        Assert.Equal("subagent_reconciliation_target_invalid", wrongTarget.Code);
        Assert.Equal(beforeDecision, await storage.EventStore.GetRunAsync(agent.RootRunId, default));
        var proposed = Assert.Single(required, e => e.Kind == "tool.proposed" && e.ToolCallId == target.ToolCallId);
        failpoint.CommandText = "UPDATE agent_events SET payload_id=NULL WHERE event_id=$proof";
        failpoint.Parameters.AddWithValue("$proof", proposed.EventId.Value);
        await failpoint.ExecuteNonQueryAsync();
        var missingProof = await Assert.ThrowsAsync<HarnessStoreException>(() => coordinator.ApplyReconciliationAsync(agent.RootRunId, command, default));
        Assert.Equal("subagent_reconciliation_call_invalid", missingProof.Code);
        Assert.Equal(beforeDecision, await storage.EventStore.GetRunAsync(agent.RootRunId, default));
        failpoint.CommandText = "UPDATE agent_events SET payload_id=$payload WHERE event_id=$proof";
        failpoint.Parameters.AddWithValue("$payload", proposed.PayloadId!.Value.Value);
        await failpoint.ExecuteNonQueryAsync();
        failpoint.CommandText = "CREATE TRIGGER fixture_fail_ack BEFORE INSERT ON agent_subagent_apply_resolutions BEGIN SELECT RAISE(FAIL,'fixture_ack_failure'); END";
        await failpoint.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<HarnessStoreException>(() => coordinator.ApplyReconciliationAsync(agent.RootRunId, command, default));
        Assert.Equal(beforeDecision, await storage.EventStore.GetRunAsync(agent.RootRunId, default));
        failpoint.CommandText = "SELECT count(*) FROM agent_events WHERE run_id=$root AND kind='reconciliation.resolved' AND safe_code=$decision";
        failpoint.Parameters.AddWithValue("$root", agent.RootRunId.Value);
        failpoint.Parameters.AddWithValue("$decision", resolutionToken);
        Assert.Equal(0L, await failpoint.ExecuteScalarAsync());
        failpoint.CommandText = "DROP TRIGGER fixture_fail_ack";
        await failpoint.ExecuteNonQueryAsync();

        var resumed = await coordinator.ApplyReconciliationAsync(agent.RootRunId, command, default);
        Assert.Equal(HarnessRunState.Running, resumed.State);
        var events = new List<HarnessEvent>();
        await foreach (var envelope in storage.EventStore.ReadEventsAsync(agent.RootRunId, 0, default)) events.Add(envelope.Event);
        var resolution = Assert.Single(events, e => e.Kind == "reconciliation.resolved" && e.SafeCode == resolutionToken);
        Assert.Equal(unknown.ExecutionId, resolution.ExecutionId);
        Assert.Equal(unknown.ToolCallId, resolution.ToolCallId);
        Assert.Equal(requiresInspection, ReconciliationGateProjection.IsMutationDenied(events));
        // The explicit user decision permits a later result to use only proved
        // receipts as its base; the unreceipted write must not become a new base.
        await AssertBasesAsync(journal);

        // Reconstruct a fresh coordinator's gate with the same durable projection
        // used by startup recovery, rather than retaining the old in-memory flag.
        gate = new ReconciliationInspectionGate();
        if (requiresInspection) gate.Activate(agent.RootRunId);
        coordinator = await Coordinator(gate);
        if (requiresInspection)
        {
            var blocked = await coordinator.RunOneStepAsync(agent.RootRunId, default);
            Assert.Equal("inspection_required", Assert.Single(blocked.Receipts).Code);
            Assert.Equal("later parent edit", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
        }
        var inspection = await coordinator.RunOneStepAsync(agent.RootRunId, default);
        Assert.Equal(ToolExecutionOutcome.Succeeded, Assert.Single(inspection.Receipts).Outcome);
        Assert.False(gate.IsMutationDenied(agent.RootRunId));
        var repeated = await coordinator.RunOneStepAsync(agent.RootRunId, default);
        Assert.Equal(HarnessRunState.ReconciliationRequired, repeated.Run.State);
        Assert.Equal(ToolExecutionOutcome.OutcomeUnknown, Assert.Single(repeated.Receipts).Outcome);
        Assert.Equal("later parent edit", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
        var unchanged = (await journal.ReadAsync(agent.RootRunId.Value, agent.AgentId, 1, default))!;
        Assert.Equal(saved.Outcome, unchanged.Outcome);
        Assert.Equal(saved.Files, unchanged.Files);
        var restartedJournal = new SqliteSubagentApplyJournal(storage.Options, new WorkspaceJournalProtector(storage.KeyStore));
        await AssertBasesAsync(restartedJournal);
        failpoint.CommandText = "SELECT count(*) FROM agent_subagent_apply_resolutions WHERE root_run_id=$root";
        Assert.Equal(1L, await failpoint.ExecuteScalarAsync());
        failpoint.CommandText = "DELETE FROM agent_events WHERE event_id=$resolution";
        failpoint.Parameters.AddWithValue("$resolution", resolution.EventId.Value);
        await failpoint.ExecuteNonQueryAsync();
        var lostProof = await Assert.ThrowsAsync<HarnessStoreException>(() => restartedJournal.ReadAppliedBaseHashesAsync(
            agent.RootRunId.Value, agent.AgentId, 2, default));
        Assert.Equal("subagent_apply_reconciliation_required", lostProof.Code);

        async Task AssertBasesAsync(SqliteSubagentApplyJournal reader)
        {
            var bases = await reader.ReadAppliedBaseHashesAsync(agent.RootRunId.Value, agent.AgentId, 2, default);
            if (failFinish) Assert.Equal(Assert.Single(saved.Files).ProposedHash, Assert.Single(bases).Value);
            else Assert.Empty(bases);
        }
    }

    private sealed class ApplyRecoveryModel(string agentId, bool requiresInspection) : IModelStepRunner
    {
        private int _calls;
        public Task<ModelStepResult> RunStepAsync(ModelStepRequest request, Func<AssistantDelta, ValueTask> delta, CancellationToken ct)
        {
            var call = ++_calls;
            var tool = requiresInspection && call == 2 ? "write"
                : call == (requiresInspection ? 3 : 2) ? "read" : "agent_apply";
            var args = tool switch
            {
                "write" => JsonSerializer.SerializeToElement(new { op = "write", path = "a.txt", content = "must not write" }),
                "read" => JsonSerializer.SerializeToElement(new { path = "a.txt" }),
                _ => JsonSerializer.SerializeToElement(new { agent_id = agentId, result_version = 1 }),
            };
            var step = new AssistantStep("recovering", [new("provider_recovery_" + call, tool, "1.0.0", args)], AssistantFinishReason.ToolCalls, 16);
            return Task.FromResult(new ModelStepResult(step, SystemPromptComposer.Compose(request.PromptContext, nativeToolCalling: true).Receipt));
        }
    }
}
