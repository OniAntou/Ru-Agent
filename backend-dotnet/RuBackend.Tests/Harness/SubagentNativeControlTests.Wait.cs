using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence.Subagents;
using RuBackend.Business.Settings;
using RuBackend.Harness;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentNativeControlTests
{
    private static async Task VerifyDeferredWaitAsync(BackendHarnessTestFixture storage, InProcessExecutionFixture execution,
        AgentRun root, string repo, PolicySnapshot policy, string agentId)
    {
        await storage.PayloadStore.CaptureBaseContextAsync(root.RunId, [], DateTimeOffset.UtcNow, default);
        var model = new WaitModel(agentId);
        var factoryDeps = SubagentCoordinatorFactoryTestDependencies.Create(storage, execution.Factory);
        var factory = new AgentRuntimeCoordinatorFactory(model, storage.EventStore, storage.Options, storage.PayloadStore,
            inspectionGate: new RuAgent.Harness.Core.Reconciliation.ReconciliationInspectionGate(),
            workspaceCoordinatorStore: new RuAgent.Harness.Persistence.WorkspaceCoordinatorStore(storage.Options),
            settings: new NativeSettings(), time: new SystemTimeProvider(), contextLimit: new StaticModelContextLimitProvider(100000),
            brokerGateways: execution.Factory, subagentBinder: factoryDeps.Binder,
            familyBudget: factoryDeps.FamilyBudget, subagentInbox: factoryDeps.Inbox,
            transientRedactor: RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor.Empty);
        var coordinator = await factory.CreateAsync(repo, policy, root.RunId, root.SessionId.Value);
        var parked = await coordinator.RunOneStepAsync(root.RunId, default);
        Assert.Equal(HarnessRunState.WaitingSubagents, parked.Run.State);
        Assert.Empty(parked.Receipts);
        var pending = Assert.Single(await storage.EventStore.ListPendingSubagentWaitsAsync(null, default));
        Assert.Equal(root.RunId, pending);
        Assert.Equal("run_not_runnable", (await coordinator.RunOneStepAsync(root.RunId, default)).ErrorCode);
        Assert.Equal(1, model.Calls);
        using var waits = new SubagentWaitCoordinator(storage.EventStore,
            new SqliteSubagentResultStore(storage.Options, storage.PayloadStore), new SystemTimeProvider());
        await waits.TickAsync(default);
        var resumed = (await storage.EventStore.GetRunAsync(root.RunId, default))!;
        Assert.Equal(HarnessRunState.Running, resumed.State);
        Assert.Equal(parked.Run.BudgetWindow, resumed.BudgetWindow);
        Assert.Equal(parked.Run.ModelStepsInWindow, resumed.ModelStepsInWindow);
        Assert.Equal(parked.Run.ToolCallsInWindow, resumed.ToolCallsInWindow);
        Assert.Equal(1, model.Calls);
        Assert.Empty(await storage.EventStore.ListPendingSubagentWaitsAsync(null, default));
    }

    private sealed class WaitModel(string agentId) : IModelStepRunner
    {
        public int Calls { get; private set; }
        public Task<ModelStepResult> RunStepAsync(ModelStepRequest request, Func<AssistantDelta, ValueTask> delta, CancellationToken ct)
        {
            Calls++;
            var args = JsonSerializer.SerializeToElement(new { agent_ids = new[] { agentId }, timeout_ms = 0 });
            var step = new AssistantStep("waiting", [new("provider_wait", "agent_wait", "1.0.0", args)], AssistantFinishReason.ToolCalls, 16);
            return Task.FromResult(new ModelStepResult(step, SystemPromptComposer.Compose(request.PromptContext, nativeToolCalling: true).Receipt));
        }
    }

    private sealed class NativeSettings : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken ct) => Task.FromResult(new SettingsSnapshot(
            new JsonObject(), "OpenAI", "fixture-model", "", "https://example.invalid/v1", "fixture-model", ""));
        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken ct) => Task.CompletedTask;
    }
}
