using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class SubagentScopeStoreTests
{
    [Fact]
    public async Task Current_schema_rejects_scope_table_with_obsolete_cascading_owner()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE agent_subagent_scopes;
            CREATE TABLE agent_subagent_scopes (
                run_id TEXT PRIMARY KEY REFERENCES agent_subagent_attempts(run_id) ON DELETE CASCADE,
                protection_version INTEGER NOT NULL, nonce BLOB NOT NULL,
                ciphertext BLOB NOT NULL, authentication_tag BLOB NOT NULL);
            """;
        await command.ExecuteNonQueryAsync();
        Assert.False((await HarnessSqliteSchema.InitializeAsync(fixture.Options, default)).IsAvailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_scope_survives_relation_loss_until_owned_run_deletion(bool migrateFrom24)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        var policy = new BrokerPolicySnapshotWire(1, "standard", new(workspace.WorkspaceId.Value,
            workspace.VolumeSerialNumber, workspace.RootFileId, workspace.DisplayIdentityHash), [],
            HarnessStoreTestFixture.Timestamp.ToString("O"));
        var root = WithPolicy(fixture.NewRun(1, 1, 1), policy);
        var child = WithPolicy(fixture.NewRun(2, 2, 1), policy);
        var events = fixture.CreateEventStore();
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child), default);
        var crypto = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var store = new SqliteSubagentScopeStore(fixture.Options, crypto);
        var scope = new RuAgent.Harness.Core.Subagents.SubagentRuntimeScope(fixture.RootPath, fixture.RootPath,
            new(root.Run.PolicySnapshotVersion, root.Run.PolicySnapshotSha256),
            BrokerPolicyContracts.WriteSnapshot(policy).ToJsonString(), null,
            BrokerPolicyContracts.WriteSnapshot(policy).ToJsonString());
        await store.SaveAsync(root.Run.RunId, child.Run.RunId, scope, default);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        const string readEnvelope = "SELECT protection_version || ':' || hex(nonce) || ':' || hex(ciphertext) || ':' || hex(authentication_tag) FROM agent_subagent_scopes";
        command.CommandText = readEnvelope;
        var encrypted = await command.ExecuteScalarAsync();
        if (migrateFrom24)
        {
            await fixture.ResetSessionHistorySchemaToV29Async();
            // Recreate the actual old FK shape rather than only relabeling a new database.
            command.CommandText = """
                PRAGMA foreign_keys=OFF;
                DROP TABLE agent_subagent_apply_resolutions;
                DROP TABLE agent_subagent_workspace_allocations;
                CREATE TABLE old_scopes (
                    run_id TEXT PRIMARY KEY REFERENCES agent_subagent_attempts(run_id) ON DELETE CASCADE,
                    protection_version INTEGER NOT NULL CHECK(protection_version=1),
                    nonce BLOB NOT NULL CHECK(length(nonce)=12), ciphertext BLOB NOT NULL,
                    authentication_tag BLOB NOT NULL CHECK(length(authentication_tag)=16));
                INSERT INTO old_scopes SELECT * FROM agent_subagent_scopes;
                DROP TABLE agent_subagent_scopes;
                ALTER TABLE old_scopes RENAME TO agent_subagent_scopes;
                PRAGMA user_version=24;
                PRAGMA foreign_keys=ON;
                """;
            await command.ExecuteNonQueryAsync();
            var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);
            Assert.True(health.IsAvailable, health.ErrorCode);
            Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
            Assert.True((await HarnessSqliteSchema.InitializeAsync(fixture.Options, default)).IsAvailable);
        }
        command.CommandText = readEnvelope;
        Assert.Equal(encrypted, await command.ExecuteScalarAsync());
        command.CommandText = "DELETE FROM agent_subagents; DELETE FROM agent_subagent_families;";
        await command.ExecuteNonQueryAsync();
        Assert.Null(await store.GetAsync(child.Run.RunId, default));
        Assert.Equal(scope, await new SqliteSubagentScopeStore(fixture.Options, crypto)
            .GetRetainedForCleanupAsync(root.Run.RunId, child.Run.RunId, default));
        var denied = await Assert.ThrowsAsync<HarnessStoreException>(() => store.GetRetainedForCleanupAsync(
            HarnessStoreTestFixture.Run(3), child.Run.RunId, default));
        Assert.Equal("subagent_cleanup_ownership_denied", denied.Code);
        Assert.Equal(root.Run.RunId, Assert.Single(await agents.ListFamiliesForSessionAsync(root.Run.SessionId, null, default)));
        var artifacts = new HarnessSessionArtifactStore(fixture.Options);
        await artifacts.MarkSessionDeletedAsync(root.Run.SessionId.Value, HarnessStoreTestFixture.Timestamp, default);
        await artifacts.CloseAndDeleteAsync(root.Run.SessionId.Value, HarnessStoreTestFixture.Timestamp, default);
        Assert.Null(await events.GetRunAsync(child.Run.RunId, default));
        command.CommandText = "SELECT count(*) FROM agent_subagent_scopes";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
