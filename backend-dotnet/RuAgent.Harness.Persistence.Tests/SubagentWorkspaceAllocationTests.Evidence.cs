using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class SubagentWorkspaceAllocationTests
{
    [Fact]
    public async Task Evidence_is_encrypted_immutable_and_survives_restart_without_a_child_relation()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var protector = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var store = new SqliteSubagentWorkspaceAllocationStore(fixture.Options, protector);
        var evidence = await store.ReserveAsync(root.Run.RunId, "agent_one", default);
        Assert.Equal(evidence, Assert.Single(await store.ListAsync(root.Run.RunId, default)));
        evidence = evidence with { Stage = SubagentWorkspaceAllocationStage.SnapshotAllocated, SnapshotJson = "{\"path\":\"private-captured-path\"}" };
        await store.RecordAsync(evidence, default);
        await store.RecordAsync(evidence, default);
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.RecordAsync(evidence with { SnapshotJson = "{}" }, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.RecordAsync(evidence with { Stage = SubagentWorkspaceAllocationStage.Prepared }, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.RecordAsync(evidence with { SnapshotId = "snapshot_other" }, default));
        for (var stage = SubagentWorkspaceAllocationStage.RegisteringWorktree; stage <= SubagentWorkspaceAllocationStage.Prepared; stage++)
        {
            evidence = evidence with { Stage = stage,
                WorktreeManifestJson = stage >= SubagentWorkspaceAllocationStage.WorktreeRegistered ? "{\"path\":\"private-worktree-path\"}" : null };
            await store.RecordAsync(evidence, default);
        }
        Assert.Equal(evidence, Assert.Single(await new SqliteSubagentWorkspaceAllocationStore(fixture.Options, protector).ListAsync(root.Run.RunId, default)));
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT ciphertext FROM agent_subagent_workspace_allocations";
        Assert.DoesNotContain("private-captured-path", System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(await query.ExecuteScalarAsync())));
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        Assert.Equal(root.Run.RunId, Assert.Single(await agents.ListFamiliesForSessionAsync(root.Run.SessionId, null, default)));
        Assert.Empty(await agents.ListOwnedRunIdsAsync(root.Run.RunId, default));
        var artifacts = new HarnessSessionArtifactStore(fixture.Options);
        await artifacts.MarkSessionDeletedAsync(root.Run.SessionId.Value, HarnessStoreTestFixture.Timestamp, default);
        Assert.Equal(evidence, Assert.Single(await store.ListAsync(root.Run.RunId, default)));
        await artifacts.CloseAndDeleteAsync(root.Run.SessionId.Value, HarnessStoreTestFixture.Timestamp, default);
        Assert.Empty(await store.ListAsync(root.Run.RunId, default));
    }

    [Fact]
    public async Task Outstanding_workspace_reservations_do_not_block_another_child()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var store = new SqliteSubagentWorkspaceAllocationStore(fixture.Options, new(new FakeHarnessKeyStore()));
        // Twelve uncommitted reservations used to exhaust the eight-identity family ceiling.
        for (var n = 0; n < 12; n++)
            await store.ReserveAsync(root.Run.RunId, "agent_" + n, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var created = await agents.CreateAsync(
            new("agent_extra", root.Run.RunId, "spawn_extra", SubagentRole.Review, fixture.NewRun(2, 2, 1)), default);
        Assert.Equal("agent_extra", created.AgentId);
        Assert.Single(await agents.ListAsync(root.Run.RunId, default));
    }
}
