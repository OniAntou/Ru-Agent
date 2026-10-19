using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class SubagentWorkspaceAllocationTests
{
    [Fact]
    public async Task Exhausted_parent_budget_denies_preparation_before_reserving_storage()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_runs SET model_steps_in_window=129";
        await command.ExecuteNonQueryAsync();
        var store = new SqliteSubagentWorkspaceAllocationStore(fixture.Options, new(new FakeHarnessKeyStore()));
        var error = await Assert.ThrowsAsync<HarnessStoreException>(() => store.ReserveAsync(root.Run.RunId, "agent_one", default));
        Assert.Equal("subagent_family_budget_exhausted", error.Code);
        Assert.Empty(await store.ListAsync(root.Run.RunId, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_session_deletion_deny_new_preparation_and_transitions(bool deleteSession)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var store = new SqliteSubagentWorkspaceAllocationStore(fixture.Options, new(new FakeHarnessKeyStore()));
        var reserved = await store.ReserveAsync(root.Run.RunId, "agent_one", default);
        if (deleteSession)
            await new HarnessSessionArtifactStore(fixture.Options).MarkSessionDeletedAsync(root.Run.SessionId.Value, HarnessStoreTestFixture.Timestamp, default);
        else await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CancelFamilyAsync(root.Run.RunId, default);
        var denied = await Assert.ThrowsAsync<HarnessStoreException>(() => store.ReserveAsync(root.Run.RunId, "agent_two", default));
        Assert.Equal(deleteSession ? "session_deleted" : "subagent_root_inactive", denied.Code);
        var transition = reserved with { Stage = SubagentWorkspaceAllocationStage.SnapshotAllocated, SnapshotJson = "{}" };
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.RecordAsync(transition, default));
        Assert.Equal(reserved, Assert.Single(await store.ListAsync(root.Run.RunId, default)));
    }

    [Fact]
    public async Task Invalid_oversized_and_corrupt_evidence_never_replaces_committed_ownership()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var store = new SqliteSubagentWorkspaceAllocationStore(fixture.Options, new(new FakeHarnessKeyStore()));
        var reserved = await store.ReserveAsync(root.Run.RunId, "agent_one", default);
        var first = reserved with { Stage = SubagentWorkspaceAllocationStage.SnapshotAllocated, SnapshotJson = "{}" };
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.RecordAsync(first with { SnapshotJson = "[]" }, default));
        var large = await Assert.ThrowsAsync<HarnessStoreException>(() => store.RecordAsync(first with {
            SnapshotJson = "{\"path\":\"" + new string('a', SubagentWorkspaceAllocation.MaximumEvidenceBytes) + "\"}" }, default));
        Assert.Equal("subagent_allocation_evidence_too_large", large.Code);
        Assert.Equal(reserved, Assert.Single(await store.ListAsync(root.Run.RunId, default)));
        await store.RecordAsync(first, default);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var corrupt = connection.CreateCommand();
        corrupt.CommandText = "UPDATE agent_subagent_workspace_allocations SET authentication_tag=zeroblob(16)";
        await corrupt.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.ListAsync(root.Run.RunId, default));
    }
}
