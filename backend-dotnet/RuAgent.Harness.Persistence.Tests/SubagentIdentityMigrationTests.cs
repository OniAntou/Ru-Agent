using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentIdentityMigrationTests
{
    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    public async Task Earlier_versions_retain_root_bound_internal_identity_without_changing_existing_runs(int version)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        var child = fixture.NewRun(2, 2, 1);
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child), default);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE agent_subagent_apply_resolutions; "
            + (version < 26 ? "DROP TABLE agent_subagent_workspace_allocations; " : "")
            + (version < 24 ? "DROP TABLE agent_subagent_result_outcomes; " : "")
            + (version < 23 ? "DROP TABLE agent_subagent_change_chunks; DROP TABLE agent_subagent_change_sets; " : "")
            + (version == 21 ? "DROP TABLE agent_internal_runs; " : "") + $"PRAGMA user_version={version}";
        await command.ExecuteNonQueryAsync();
        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);
        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        Assert.Equal(child.Run.RunId, Assert.Single(await agents.ListOwnedRunIdsAsync(root.Run.RunId, default)));
        Assert.True(await agents.IsInternalRunAsync(child.Run.RunId, default));
        Assert.False(await agents.IsInternalRunAsync(root.Run.RunId, default));
        Assert.Equal(root.Run, await events.GetRunAsync(root.Run.RunId, default));
        Assert.Equal(child.Run, await events.GetRunAsync(child.Run.RunId, default));
        Assert.True((await HarnessSqliteSchema.InitializeAsync(fixture.Options, default)).IsAvailable);
        command.CommandText = "DELETE FROM agent_subagents WHERE agent_id='agent_one'";
        await command.ExecuteNonQueryAsync();
        Assert.True(await agents.IsInternalRunAsync(child.Run.RunId, default));
        Assert.Equal(child.Run.RunId, Assert.Single(await agents.ListOwnedRunIdsAsync(root.Run.RunId, default)));
    }
}
