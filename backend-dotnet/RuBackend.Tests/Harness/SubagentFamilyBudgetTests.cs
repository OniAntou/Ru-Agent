using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Subagents;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentFamilyBudgetTests
{
    [Theory]
    [InlineData(HarnessRunState.WaitingUser)]
    [InlineData(HarnessRunState.WaitingApproval)]
    [InlineData(HarnessRunState.WaitingSubagents)]
    public async Task Parked_root_allows_only_budgeted_checkpoint_calls(HarnessRunState state)
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        var root = await storage.SeedRunAsync(new(state));
        var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
        var gate = new SubagentFamilyBudget(storage.EventStore, agents, new SqliteSubagentBudgetStore(storage.Options), new SystemTimeProvider());
        var step = new ModelStepId("step_" + new string('f', 32));
        Assert.False(await gate.ReserveModelAsync(root.Run, step, default));
        Assert.True(await gate.ReserveCheckpointAsync(root.Run, step, default));
    }

    [Fact]
    public async Task Root_and_child_share_exact_reservations_and_cancel_revokes_even_replayed_charge()
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        var root = await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + new string('a', 32), new string('a', 32), storage.RootPath);
        await using (var connection = await storage.Options.OpenConnectionAsync(default))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_runs SET model_steps_in_window=127,tool_calls_in_window=254";
            await command.ExecuteNonQueryAsync();
        }
        var identity = await new WindowsWorkspaceIdentityResolver().ResolveAsync(storage.RootPath, default);
        var policy = PolicySnapshotCanonicalizer.Create(1, root.Run.Preset, identity, [], DateTimeOffset.UtcNow);
        var json = BrokerPolicyContracts.WriteSnapshot(PolicySnapshotMapper.ToWire(policy)).ToJsonString();
        var child = new AgentRun(new("run_" + new string('b', 32)), new(new string('b', 32)), identity.WorkspaceId,
            new(HarnessRunState.Created), 0, 1, 1, 0, 0, policy.Preset, policy.Version, policy.Sha256, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review,
            new(child, [new(new("evt_" + new string('b', 32)), HarnessEventKind.RunStateChanged)], [], [],
                new(new string('a', 64), new string('b', 64), 1))), default);
        await new SqliteAgentRunProviderSelectionStore(storage.Options).PinAsync(child.RunId,
            new("Custom OpenAI", "custom", "chat_completions", "https://example.invalid/v1/", "fixture", null, "{}", null), default);
        await storage.PayloadStore.CaptureBaseContextAsync(child.RunId, [], DateTimeOffset.UtcNow, default);
        await new SqliteSubagentScopeStore(storage.Options, new WorkspaceJournalProtector(storage.KeyStore)).SaveAsync(root.Run.RunId,
            child.RunId, new(storage.RootPath, storage.RootPath, new(policy.Version, policy.Sha256), json, null, json), default);
        Assert.True(await agents.TryAdmitAsync("agent_one", DateTimeOffset.UtcNow, default));
        var actor = (await storage.EventStore.GetRunAsync(child.RunId, default))!;
        var gate = new SubagentFamilyBudget(storage.EventStore, agents, new SqliteSubagentBudgetStore(storage.Options), new SystemTimeProvider());
        var step = new ModelStepId("step_" + new string('1', 32));
        Assert.True(await gate.ReserveModelAsync(actor, step, default));
        Assert.True(await gate.ReserveModelAsync(actor, step, default));
        Assert.True(await gate.ReserveCheckpointAsync(actor, step, default));
        Assert.False(await gate.ReserveModelAsync(actor, new("step_" + new string('2', 32)), default));
        Assert.False(await gate.ReserveModelAsync((await storage.EventStore.GetRunAsync(root.Run.RunId, default))!,
            new("step_" + new string('3', 32)), default));
        Assert.True(await gate.ReserveToolsAsync(actor, step, 2, default));
        Assert.True(await gate.ReserveToolsAsync(actor, step, 2, default));
        Assert.False(await gate.ReserveToolsAsync(actor, new("step_" + new string('2', 32)), 1, default));
        await agents.CancelFamilyAsync(root.Run.RunId, default);
        Assert.False(await gate.ReserveModelAsync(actor, step, default));
        Assert.False(await gate.ReserveCheckpointAsync(actor, step, default));
        await using (var connection = await storage.Options.OpenConnectionAsync(default))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM agent_subagents WHERE agent_id='agent_one'";
            await command.ExecuteNonQueryAsync();
        }
        Assert.False(await gate.ReserveModelAsync(actor, new("step_" + new string('4', 32)), default));
    }
}
