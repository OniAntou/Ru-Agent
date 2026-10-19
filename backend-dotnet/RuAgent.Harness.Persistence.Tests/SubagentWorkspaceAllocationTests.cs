using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class SubagentWorkspaceAllocationTests
{
    [Fact]
    public async Task Reservations_atomically_bound_work_before_any_child_run_exists()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var store = new SqliteSubagentWorkspaceAllocationStore(fixture.Options, new(new FakeHarnessKeyStore()));
        var requests = Enumerable.Range(0, 12).Select(async n =>
        {
            try { return await store.ReserveAsync(root.Run.RunId, "agent_" + n, default); }
            catch (HarnessStoreException error) { Assert.Equal("subagent_identity_limit", error.Code); return null; }
        });
        var allocated = (await Task.WhenAll(requests)).Where(r => r is not null).ToArray();
        Assert.Equal(SubagentLimits.MaxIdentities, allocated.Length);
        Assert.Equal(allocated.Length, allocated.Select(r => r!.SnapshotId).Distinct().Count());
        Assert.All(allocated, r => Assert.Equal(SubagentWorkspaceAllocationStage.Reserved, r!.Stage));
        var duplicate = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            store.ReserveAsync(root.Run.RunId, allocated[0]!.AgentId, default));
        Assert.Equal("subagent_preparation_incomplete", duplicate.Code);
    }
}
