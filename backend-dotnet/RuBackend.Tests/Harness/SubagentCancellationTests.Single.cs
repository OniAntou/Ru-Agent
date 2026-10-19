using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
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
    public async Task Single_child_stop_commits_tombstone_before_cleanup_and_preserves_parent_and_sibling(bool cleanupFails)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var root = await fixture.SeedRunAsync(new(HarnessRunState.Running));
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        AgentRun Child(char id) => new(new("run_" + new string(id, 32)), new(new string(id, 32)), root.Run.WorkspaceId,
            new(HarnessRunState.Created), 0, 1, 1, 0, 0, root.Run.Preset, root.Run.PolicySnapshotVersion,
            root.Run.PolicySnapshotSha256, BackendHarnessTestFixture.Timestamp, BackendHarnessTestFixture.Timestamp);
        var first = Child('b');
        var sibling = Child('c');
        foreach (var (run, agent) in new[] { (first, "agent_one"), (sibling, "agent_two") })
            await agents.CreateAsync(new(agent, root.Run.RunId, "spawn_" + agent, SubagentRole.Review,
                new(run, [new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.RunStateChanged)], [], [],
                    new(new string('a', 64), new string('b', 64), 1))), default);
        var cleanup = new SingleCleanup(agents, root.Run.RunId, first.RunId) { FailOnce = cleanupFails };
        var cancellation = new SubagentCancellationCoordinator(fixture.EventStore, agents, cleanup, new SystemTimeProvider());
        if (cleanupFails)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cancellation.CancelAsync(root.Run.RunId, "agent_one", default));
            Assert.Equal("subagent_cancel_incomplete", error.Message);
            Assert.True(Assert.Single(await agents.ListAsync(root.Run.RunId, default), a => a.AgentId == "agent_one").Canceled);
        }
        await cancellation.CancelAsync(root.Run.RunId, "agent_one", default);
        var canceled = (await fixture.EventStore.GetRunAsync(first.RunId, default))!;
        Assert.Equal(HarnessRunState.Cancelled, canceled.State);
        Assert.Equal("subagent_canceled", canceled.TerminalErrorCode);
        await cancellation.CancelAsync(root.Run.RunId, "agent_one", default);
        Assert.Equal(canceled, await fixture.EventStore.GetRunAsync(first.RunId, default));
        Assert.All(cleanup.Runs, id => Assert.Equal(first.RunId, id));
        Assert.Equal(root.Run, await fixture.EventStore.GetRunAsync(root.Run.RunId, default));
        Assert.Equal(sibling, await fixture.EventStore.GetRunAsync(sibling.RunId, default));
        await agents.AppendMessageAsync(root.Run.RunId, "agent_two", "message_two", "Sibling still usable", default);
        await Assert.ThrowsAsync<RuAgent.Harness.Persistence.HarnessStoreException>(() =>
            agents.AppendMessageAsync(root.Run.RunId, "agent_one", "message_one", "Cannot revive", default));
    }

    private sealed class SingleCleanup(SqliteSubagentStore agents, RunId root, RunId target) : IAgentRuntimeRunCleanup
    {
        public bool FailOnce { get; set; }
        public List<RunId> Runs { get; } = [];
        public async Task CancelAndRemoveRunAsync(RunId runId, CancellationToken ct)
        {
            Assert.Equal(target, runId);
            var siblings = await agents.ListAsync(root, ct);
            Assert.True(Assert.Single(siblings, a => a.AgentId == "agent_one").Canceled);
            Assert.False(Assert.Single(siblings, a => a.AgentId == "agent_two").Canceled);
            Runs.Add(runId);
            if (FailOnce) { FailOnce = false; throw new InvalidOperationException("fixture_cleanup_failure"); }
        }
    }
}
