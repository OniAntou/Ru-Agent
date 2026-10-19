using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Context;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Business.Settings;
using RuBackend.Harness;
using RuBackend.Harness.Prompting;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentDeferredWaitTests
{
    [Fact]
    public async Task Durable_result_completes_original_native_call_before_parent_resumes()
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        var root = await storage.SeedRunAsync(new(HarnessRunState.Running));
        var now = DateTimeOffset.UtcNow;
        var child = new AgentRun(new("run_" + new string('2', 32)), new(new string('2', 32)), root.Run.WorkspaceId,
            new(HarnessRunState.Created), 0, 1, 1, 0, 0, root.Run.Preset, root.Run.PolicySnapshotVersion,
            root.Run.PolicySnapshotSha256, now, now);
        await new SqliteSubagentStore(storage.Options, storage.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review,
                new(child, [new(new("evt_" + new string('2', 32)), HarnessEventKind.RunStateChanged)], [], [],
                    new(new string('a', 64), new string('b', 64), 1))), default);
        await storage.PayloadStore.CaptureBaseContextAsync(root.Run.RunId, [], now, default);
        var contextSource = new SqliteModelContextSource(storage.Options, storage.PayloadStore);
        var time = new SystemTimeProvider();
        var gateway = new AuthorizedWaitFixture();
        var model = ScriptedModelStepRunner.FromJsonForTests(
            """[{"tool":{"id":"agent_wait","arguments":{"agent_ids":["agent_one"],"timeout_ms":60000}}},{"text":"Combined findings"}]""");
        var coordinator = new AgentRunCoordinator(model, gateway, storage.EventStore, new AgentContextAssembler(contextSource),
            new CryptographicIdGenerator(), time, new ToolCallScheduler(), new TransientFrameBuffer(65536),
            contextLimit: new StaticModelContextLimitProvider(100000),
            promptContextProvider: new RuPromptContextProvider(storage.RootPath, new RuInstructionLoader(), new Settings(), time, webToolsEnabled: true));
        var parked = await coordinator.RunOneStepAsync(root.Run.RunId, default);
        Assert.Equal(HarnessRunState.WaitingSubagents, parked.Run.State);
        Assert.Empty(parked.Receipts);
        await storage.EventStore.CommitAsync(SubagentAdmission.CreateTerminalCommit(child,
            new("evt_" + Guid.NewGuid().ToString("N")), false, "subagent_blocked", DateTimeOffset.UtcNow), default);
        using var waits = new SubagentWaitCoordinator(storage.EventStore,
            new SqliteSubagentResultStore(storage.Options, storage.PayloadStore), time);
        await waits.TickAsync(default);
        var context = await contextSource.LoadAsync(root.Run.RunId, default);
        var assistant = Assert.Single(context.RunLocal, entry => entry.Kind == ModelContextEntryKind.AssistantStep);
        var tool = Assert.Single(context.RunLocal, entry => entry.Kind == ModelContextEntryKind.ToolResult);
        Assert.Equal(Assert.Single(assistant.AssistantStep!.ToolCalls).ProviderCallId, tool.ToolReceipt!.ProviderCallId);
        using var output = System.Text.Json.JsonDocument.Parse(tool.Content!);
        Assert.Equal("agent_one", output.RootElement.GetProperty("results")[0].GetProperty("agent_id").GetString());
        var final = await coordinator.RunOneStepAsync(root.Run.RunId, default);
        Assert.Equal(HarnessRunState.WaitingHistory, final.Run.State);
        Assert.Equal(1, gateway.Executions);
        Assert.Equal(root.Run.RunId, (await new HarnessOutboxStore(storage.Options).ClaimNextAsync(DateTimeOffset.UtcNow, default))!.RunId);
    }

    // This fixture represents an already authorized control call; Broker authorization
    // is exercised separately. It provides no filesystem, process or network adapter.
    private sealed class AuthorizedWaitFixture : IToolGateway
    {
        public int Executions;
        public Task<ToolProposalDecision> EvaluateAsync(ToolProposal proposal, CancellationToken ct) =>
            Task.FromResult(ToolProposalDecision.Allow(ToolAccessKind.ReadOnly, "fixture_wait"));
        public Task<PreparedToolExecution> PrepareAsync(ApprovedToolExecution approved, CancellationToken ct) =>
            Task.FromResult(new PreparedToolExecution(approved.ExecutionId, approved.Proposal.ToolCallId,
                approved.Proposal.ModelOrder, ToolAccessKind.ReadOnly, "fixture_wait", []));
        public Task<ToolExecutionReceipt> ExecuteAsync(PreparedToolExecution execution, Func<ToolOutputChunk, ValueTask> delta, CancellationToken ct)
        { Executions++; return Task.FromResult(ToolExecutionReceipt.DeferredSubagentWait(execution.ExecutionId, execution.ToolCallId)); }
        public Task CancelAsync(ExecutionId executionId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Settings : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken ct) => Task.FromResult(new SettingsSnapshot(
            new JsonObject(), "OpenAI", "fixture-model", "", "http://localhost:8000/v1", "fixture-model", ""));
        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken ct) => Task.CompletedTask;
    }
}
