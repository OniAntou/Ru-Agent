using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class SubagentWorkspaceAllocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Release_requires_exact_reservation_and_never_discards_allocation_evidence(bool allocated)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var store = new SqliteSubagentWorkspaceAllocationStore(fixture.Options, new(new FakeHarnessKeyStore()));
        var reserved = await store.ReserveAsync(root.Run.RunId, "agent_one", default);
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.ReleaseUnallocatedAsync(
            reserved with { SnapshotId = "snapshot_other" }, default));
        var retained = allocated
            ? reserved with { Stage = SubagentWorkspaceAllocationStage.SnapshotAllocated, SnapshotJson = "{}" }
            : reserved;
        if (allocated)
        {
            await store.RecordAsync(retained, default);
            await Assert.ThrowsAsync<HarnessStoreException>(() => store.ReleaseUnallocatedAsync(reserved, default));
            Assert.Equal(retained, Assert.Single(await store.ListAsync(root.Run.RunId, default)));
        }
        else
        {
            Assert.Equal(reserved, Assert.Single(await store.ListAsync(root.Run.RunId, default)));
            await store.ReleaseUnallocatedAsync(reserved, default);
            Assert.Empty(await store.ListAsync(root.Run.RunId, default));
            await store.ReserveAsync(root.Run.RunId, reserved.AgentId, default);
        }
    }
}
