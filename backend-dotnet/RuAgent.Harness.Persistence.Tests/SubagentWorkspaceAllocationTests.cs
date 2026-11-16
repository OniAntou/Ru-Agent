using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class SubagentWorkspaceAllocationTests
{
    [Fact]
    public async Task Concurrent_reservations_each_get_their_own_snapshot_with_no_family_ceiling()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var store = new SqliteSubagentWorkspaceAllocationStore(fixture.Options, new(new FakeHarnessKeyStore()));
        // Twelve is past the eight-identity ceiling this store used to enforce.
        var allocated = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(n => store.ReserveAsync(root.Run.RunId, "agent_" + n, default)));
        Assert.Equal(12, allocated.Length);
        Assert.Equal(allocated.Length, allocated.Select(r => r.SnapshotId).Distinct().Count());
        Assert.All(allocated, r => Assert.Equal(SubagentWorkspaceAllocationStage.Reserved, r.Stage));
        Assert.Equal(12, (await store.ListAsync(root.Run.RunId, default)).Count);
        var duplicate = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.ReserveAsync(root.Run.RunId, allocated[0].AgentId, default));
        Assert.Equal("subagent_preparation_incomplete", duplicate.Code);
    }
}
