using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentLivePublisherTests
{
    [Fact]
    public async Task Child_hints_are_root_scoped_deduplicated_and_never_advance_parent_version()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await fixture.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + new string('a', 32), new string('a', 32), fixture.RootPath);
        var root = seeded.Run;
        var child = new AgentRun(new("run_" + new string('b', 32)), new(new string('b', 32)), root.WorkspaceId,
            new(HarnessRunState.Created), 0, 1, 1, 0, 0, root.Preset, root.PolicySnapshotVersion, root.PolicySnapshotSha256,
            root.CreatedAt, root.CreatedAt);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        await agents.CreateAsync(new("agent_one", root.RunId, "spawn_one", SubagentRole.Review,
            new(child, [new(new("evt_" + new string('b', 32)), HarnessEventKind.RunStateChanged, safeCode: "subagent_queued")], [], [],
                new(new string('b', 64), new string('c', 64), 1))), default);
        var hub = new HarnessLiveEventHub();
        var publisher = new SubagentLivePublisher(new(fixture.Options, fixture.PayloadStore), agents, new(fixture.Options), hub);
        await using var rootFeed = hub.Subscribe(root.RunId);
        await using var otherFeed = hub.Subscribe(child.RunId);
        await publisher.PublishAsync(root.RunId, default);
        Assert.True(rootFeed.TryReadNext(out var first));
        Assert.Equal("subagent.changed", first.Kind);
        using var data = JsonDocument.Parse(first.Data);
        var entry = Assert.Single(data.RootElement.GetProperty("agents").EnumerateArray());
        Assert.Equal("agent_one", entry.GetProperty("agent_id").GetString());
        Assert.Equal(child.RunId.Value, Assert.Single(entry.GetProperty("attempts").EnumerateArray()).GetProperty("attempt_run_id").GetString());
        Assert.False(otherFeed.TryReadNext(out _));
        await publisher.PublishAsync(root.RunId, default);
        Assert.False(rootFeed.TryReadNext(out _));
        await fixture.EventStore.CommitAsync(RuAgent.Harness.Core.Subagents.SubagentAdmission.CreateTerminalCommit(
            child, new("evt_" + new string('c', 32)), true, "fixture_cancelled", DateTimeOffset.UtcNow), default);
        await publisher.PublishAsync(root.RunId, default);
        Assert.True(rootFeed.TryReadNext(out _));
        Assert.Equal(root, await fixture.EventStore.GetRunAsync(root.RunId, default));
        publisher.Forget(root.RunId);
        await publisher.PublishAsync(root.RunId, default);
        Assert.True(rootFeed.TryReadNext(out _));
        var snapshot = await new SqliteSubagentSnapshotReader(fixture.Options, fixture.PayloadStore).ReadAsync(root.RunId, default);
        Assert.Equal(HarnessRunState.Cancelled, Assert.Single(snapshot.Agents).Attempts[0].Attempt.Run.State);
    }
}
