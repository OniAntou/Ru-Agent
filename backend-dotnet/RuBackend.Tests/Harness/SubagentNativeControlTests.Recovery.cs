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
    /// <summary>
    /// A partial coding apply is an uncertain side effect, but it is a subagent
    /// control: the model receives a failed receipt with sideEffectsPossible=true
    /// and the root run stays running. It must not park for a user Continue.
    /// </summary>
    private static async Task VerifyApplyRecoveryAsync(BackendHarnessTestFixture storage,
        InProcessExecutionFixture execution, SubagentRecord agent, string repo, PolicySnapshot policy,
        string resolutionToken, bool failFinish)
    {
        _ = resolutionToken;
        await using var connection = await storage.Options.OpenConnectionAsync(default);
        using var failpoint = connection.CreateCommand();
        failpoint.CommandText = failFinish
            ? "CREATE TRIGGER fixture_fail_apply BEFORE UPDATE OF status ON agent_subagent_apply_operations BEGIN SELECT RAISE(FAIL,'fixture_finish_failure'); END"
            : "CREATE TRIGGER fixture_fail_apply BEFORE UPDATE OF receipted ON agent_subagent_apply_files BEGIN SELECT RAISE(FAIL,'fixture_receipt_failure'); END";
        await failpoint.ExecuteNonQueryAsync();
        var journal = new SqliteSubagentApplyJournal(storage.Options, new WorkspaceJournalProtector(storage.KeyStore));
        var gate = new ReconciliationInspectionGate();
        var model = new ApplyRecoveryModel(agent.AgentId);
        var factoryDeps = SubagentCoordinatorFactoryTestDependencies.Create(storage, execution.Factory);
        var coordinator = await new AgentRuntimeCoordinatorFactory(model, storage.EventStore, storage.Options, storage.PayloadStore,
                inspectionGate: gate,
                workspaceCoordinatorStore: new RuAgent.Harness.Persistence.WorkspaceCoordinatorStore(storage.Options),
                settings: new NativeSettings(), time: new SystemTimeProvider(),
                contextLimit: new StaticModelContextLimitProvider(100000), brokerGateways: execution.Factory,
                subagentBinder: factoryDeps.Binder, familyBudget: factoryDeps.FamilyBudget,
                subagentInbox: factoryDeps.Inbox,
                transientRedactor: RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor.Empty)
            .CreateAsync(repo, policy, agent.RootRunId, (await storage.EventStore.GetRunAsync(agent.RootRunId, default))!.SessionId.Value);

        var interrupted = await coordinator.RunOneStepAsync(agent.RootRunId, default);

        Assert.Equal(HarnessRunState.Running, interrupted.Run.State);
        var failed = Assert.Single(interrupted.Receipts);
        Assert.Equal(ToolExecutionOutcome.Failed, failed.Outcome);
        Assert.True(failed.SideEffectsPossible);
        Assert.Equal("subagent_apply_reconciliation_required", failed.Code);
        Assert.Equal("child", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
        var saved = (await journal.ReadAsync(agent.RootRunId.Value, agent.AgentId, 1, default))!;
        Assert.Equal(failFinish ? SubagentApplyStatus.Pending : SubagentApplyStatus.Unknown, saved.Outcome.Status);
        Assert.Equal(failFinish, Assert.Single(saved.Files).Receipted);
    }

    private sealed class ApplyRecoveryModel(string agentId) : IModelStepRunner
    {
        private int _calls;
        public Task<ModelStepResult> RunStepAsync(ModelStepRequest request, Func<AssistantDelta, ValueTask> delta, CancellationToken ct)
        {
            var call = ++_calls;
            var step = new AssistantStep("recovering",
                [new("provider_recovery_" + call, "agent_apply", "1.0.0",
                    System.Text.Json.JsonSerializer.SerializeToElement(new { agent_id = agentId, result_version = 1 }))],
                AssistantFinishReason.ToolCalls, 16);
            return Task.FromResult(new ModelStepResult(step, SystemPromptComposer.Compose(request.PromptContext).Receipt));
        }
    }
}
