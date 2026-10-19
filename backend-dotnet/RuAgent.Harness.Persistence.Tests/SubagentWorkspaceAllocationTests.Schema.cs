using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class SubagentWorkspaceAllocationTests
{
    [Fact]
    public async Task Current_schema_rejects_allocation_proof_bound_to_removable_family_metadata()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name='agent_subagent_workspace_allocations'";
        var original = Assert.IsType<string>(await command.ExecuteScalarAsync());
        var wrongOwnership = original.Replace("REFERENCES agent_runs(run_id)",
            "REFERENCES agent_subagent_families(root_run_id)", StringComparison.Ordinal);
        Assert.NotEqual(original, wrongOwnership);
        command.CommandText = "DROP TABLE agent_subagent_workspace_allocations; " + wrongOwnership;
        await command.ExecuteNonQueryAsync();

        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);
        Assert.False(health.IsAvailable);
    }
}
