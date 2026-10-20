using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generic_continue_cannot_reset_internal_attempt_budget_even_if_owner_metadata_is_lost(bool missingOwner)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var root = await fixture.SeedRunAsync(new(HarnessRunState.Running));
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var child = new AgentRun(new("run_" + new string('b', 32)), new(new string('b', 32)), root.Run.WorkspaceId,
            new(HarnessRunState.Created), 0, 1, 1, 0, 0, root.Run.Preset, root.Run.PolicySnapshotVersion,
            root.Run.PolicySnapshotSha256, BackendHarnessTestFixture.Timestamp, BackendHarnessTestFixture.Timestamp);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review,
            new(child, [new(new("evt_" + new string('b', 32)), HarnessEventKind.RunStateChanged)], [], [],
                new(new string('a', 64), new string('b', 64), 1))), default);
        await using (var connection = await fixture.Options.OpenConnectionAsync(default))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_runs SET state=$state,model_steps_in_window=32,tool_calls_in_window=64 WHERE run_id=$run";
            command.Parameters.AddWithValue("$state", (int)HarnessRunState.WaitingUser);
            command.Parameters.AddWithValue("$run", child.RunId.Value);
            await command.ExecuteNonQueryAsync();
            if (missingOwner)
            {
                command.CommandText = "DELETE FROM agent_subagents WHERE agent_id='agent_one'";
                await command.ExecuteNonQueryAsync();
            }
        }
        var before = (await fixture.EventStore.GetRunAsync(child.RunId, default))!;
        using var services = new AgentV1ServiceTestBuilder(fixture).Build();
        var response = await services.Lifecycle.ContinueAsync(child.RunId.Value, default);
        Assert.Equal(409, response.StatusCode);
        Assert.Contains("subagent_read_only", System.Text.Json.JsonSerializer.Serialize(response.Body));
        foreach (var blocked in new[] {
            await services.Lifecycle.CancelAsync(child.RunId.Value, default),
            await services.Approvals.HandleApprovalAsync(child.RunId.Value, "apr_fixture", null, default),
            await services.Inputs.HandleInputAsync(child.RunId.Value, "input_fixture", null, default),
            await services.Commands.CompactAsync(child.RunId.Value, null, default),
            await services.Commands.DispatchCommandAsync(child.RunId.Value, null, default),
            await services.Commands.ReconcileAsync(child.RunId.Value, null, null, default),
            await services.WorkspaceTrust.TrustAsync(child.RunId.Value, null!, default) })
        {
            Assert.Equal(409, blocked.StatusCode);
            Assert.Contains("subagent_read_only", System.Text.Json.JsonSerializer.Serialize(blocked.Body));
        }
        Assert.Equal(before, await fixture.EventStore.GetRunAsync(child.RunId, default));
    }

    [Fact]
    public async Task Stop_before_first_spawn_commits_tombstone_so_late_spawn_cannot_create_a_child()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var root = await fixture.SeedRunAsync(new(HarnessRunState.Running));
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var cancellation = new SubagentCancellationCoordinator(fixture.EventStore, agents,
            new Cleanup(agents, root.Run.RunId), new SystemTimeProvider());
        await cancellation.CancelFamilyAsync(root.Run.RunId, default);
        var child = new AgentRun(new("run_" + new string('b', 32)), new(new string('b', 32)), root.Run.WorkspaceId,
            new(HarnessRunState.Created), 0, 1, 1, 0, 0, root.Run.Preset, root.Run.PolicySnapshotVersion,
            root.Run.PolicySnapshotSha256, BackendHarnessTestFixture.Timestamp, BackendHarnessTestFixture.Timestamp);
        await Assert.ThrowsAsync<HarnessStoreException>(() => agents.CreateAsync(
            new("agent_late", root.Run.RunId, "spawn_late", SubagentRole.Review,
                new(child, [new(new("evt_" + new string('b', 32)), HarnessEventKind.RunStateChanged)], [], [],
                    new(new string('a', 64), new string('b', 64), 1))), default));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Stop_tombstones_family_before_canceling_workers_and_closes_all_children_idempotently(bool viaService, bool missingOwner)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var root = await fixture.SeedRunAsync(new(HarnessRunState.Running));
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var child = new AgentRun(new("run_" + new string('b', 32)), new(new string('b', 32)), root.Run.WorkspaceId,
            new(HarnessRunState.Created), 0, 1, 1, 0, 0, root.Run.Preset, root.Run.PolicySnapshotVersion,
            root.Run.PolicySnapshotSha256, BackendHarnessTestFixture.Timestamp, BackendHarnessTestFixture.Timestamp);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review,
            new(child, [new(new("evt_" + new string('b', 32)), HarnessEventKind.RunStateChanged)], [], [],
                new(new string('a', 64), new string('b', 64), 1))), default);
        if (missingOwner)
        {
            await using var connection = await fixture.Options.OpenConnectionAsync(default);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM agent_subagents WHERE agent_id='agent_one'";
            await command.ExecuteNonQueryAsync();
        }
        var cleanup = new Cleanup(agents, root.Run.RunId);
        var cancellation = new SubagentCancellationCoordinator(fixture.EventStore, agents, cleanup, new SystemTimeProvider());
        using var services = new AgentV1ServiceTestBuilder(fixture)
            .WithSubagentCancellation(cancellation)
            .Build();
        async Task StopAsync()
        {
            if (viaService) Assert.Equal(200, (await services.Lifecycle.CancelAsync(root.Run.RunId.Value, default)).StatusCode);
            else await cancellation.CancelFamilyAsync(root.Run.RunId, default);
        }
        await StopAsync();
        Assert.Contains(root.Run.RunId, cleanup.Runs);
        Assert.Contains(child.RunId, cleanup.Runs);
        var canceled = (await fixture.EventStore.GetRunAsync(child.RunId, default))!;
        Assert.Equal(HarnessRunState.Cancelled, canceled.State);
        Assert.Equal("subagent_parent_canceled", canceled.TerminalErrorCode);
        await StopAsync();
        Assert.Equal(canceled.Version, (await fixture.EventStore.GetRunAsync(child.RunId, default))!.Version);
        await Assert.ThrowsAsync<HarnessStoreException>(() => agents.AppendMessageAsync(
            root.Run.RunId, "agent_one", "message_after_stop", "restart", default));
    }

    private sealed class Cleanup(SqliteSubagentStore agents, RunId root) : IAgentRuntimeRunCleanup
    {
        public List<RunId> Runs { get; } = [];
        public async Task CancelAndRemoveRunAsync(RunId runId, CancellationToken ct)
        {
            Assert.All(await agents.ListAsync(root, ct), agent => Assert.True(agent.Canceled));
            Runs.Add(runId);
        }
    }
}
