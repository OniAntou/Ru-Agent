using System.Security.Cryptography;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentChangeSetStoreTests
{
    [Theory]
    [InlineData("review")]
    [InlineData("version")]
    [InlineData("foreign_root")]
    [InlineData("duplicate")]
    [InlineData("traversal")]
    [InlineData("hash")]
    public async Task Invalid_or_foreign_changes_never_leave_partial_storage(string scenario)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        var child = fixture.NewRun(2, 2, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", scenario == "review" ? SubagentRole.Review : SubagentRole.Coding, child), default);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_runs SET state=$state,terminal_at_utc=updated_at_utc WHERE run_id=$run";
        command.Parameters.AddWithValue("$state", (int)HarnessRunState.Completed);
        command.Parameters.AddWithValue("$run", child.Run.RunId.Value);
        await command.ExecuteNonQueryAsync();
        var bytes = new byte[] { 1, 2, 3 };
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var file = new SubagentFileChange(scenario == "traversal" ? "../outside" : "a.txt", "missing",
            scenario == "hash" ? new string('a', 64) : hash, bytes, 100644);
        var changes = new SubagentChangeSet("agent_one", scenario == "foreign_root" ? HarnessStoreTestFixture.Run(3).Value : root.Run.RunId.Value,
            fixture.RootPath, "snapshot_one", scenario == "version" ? 2 : 1,
            scenario == "duplicate" ? [file, file with { RelativePath = "A.txt" }] : [file]);
        var store = new SqliteSubagentChangeSetStore(fixture.Options, new WorkspaceJournalProtector(new FakeHarnessKeyStore()));
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.SaveAsync(child.Run.RunId, changes, default));
        command.CommandText = "SELECT (SELECT count(*) FROM agent_subagent_change_sets)+(SELECT count(*) FROM agent_subagent_change_chunks)";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("tamper")]
    [InlineData("missing")]
    [InlineData("swap")]
    public async Task Frozen_binary_content_survives_restart_rejects_substitution_and_is_removed_with_parent(string corruption)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        var child = fixture.NewRun(2, 2, 1);
        await events.TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Coding, child), default);
        var bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 17);
        var expected = bytes.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var changes = new SubagentChangeSet("agent_one", root.Run.RunId.Value, fixture.RootPath, "snapshot_one", 1,
            [new("binary.dat", "missing", hash, bytes, 100644), new("deleted.txt", new string('a', 64), "missing", ReadOnlyMemory<byte>.Empty, 100644)]);
        var crypto = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var store = new SqliteSubagentChangeSetStore(fixture.Options, crypto);
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.SaveAsync(child.Run.RunId, changes, default));
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_runs SET state=$state,updated_at_utc=$now,terminal_at_utc=$now WHERE run_id=$run";
        command.Parameters.AddWithValue("$state", (int)HarnessRunState.Completed);
        command.Parameters.AddWithValue("$now", "2026-10-18T00:00:00Z");
        command.Parameters.AddWithValue("$run", child.Run.RunId.Value);
        await command.ExecuteNonQueryAsync();
        await store.SaveAsync(child.Run.RunId, changes, default);
        await store.SaveAsync(child.Run.RunId, changes, default);
        bytes[0] ^= 1;
        var restarted = new SqliteSubagentChangeSetStore(fixture.Options, crypto);
        if (corruption != "none")
        {
            command.CommandText = corruption switch
            {
                "missing" => "DELETE FROM agent_subagent_change_chunks WHERE chunk_index=1",
                "swap" => "UPDATE agent_subagent_change_chunks SET nonce=(SELECT nonce FROM agent_subagent_change_chunks WHERE chunk_index=1),ciphertext=(SELECT ciphertext FROM agent_subagent_change_chunks WHERE chunk_index=1),authentication_tag=(SELECT authentication_tag FROM agent_subagent_change_chunks WHERE chunk_index=1) WHERE chunk_index=0",
                _ => "UPDATE agent_subagent_change_chunks SET ciphertext=zeroblob(length(ciphertext)) WHERE chunk_index=1",
            };
            await command.ExecuteNonQueryAsync();
            var error = await Assert.ThrowsAsync<HarnessStoreException>(() => restarted.GetAsync(root.Run.RunId, "agent_one", child.Run.RunId, default));
            Assert.Equal(corruption == "missing" ? "subagent_changes_chunk_missing" : "journal_authentication_failed", error.Code);
            return;
        }
        var frozen = (await restarted.GetAsync(root.Run.RunId, "agent_one", child.Run.RunId, default))!;
        Assert.Equal(expected, frozen.Files[0].Content.ToArray());
        Assert.Equal("missing", frozen.Files[1].ProposedHash);
        await Assert.ThrowsAsync<HarnessStoreException>(() => restarted.SaveAsync(child.Run.RunId,
            changes with { Files = [changes.Files[0] with { ProposedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() }, changes.Files[1]] }, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => restarted.GetAsync(HarnessStoreTestFixture.Run(3), "agent_one", child.Run.RunId, default));
        var artifacts = new HarnessSessionArtifactStore(fixture.Options);
        await artifacts.MarkSessionDeletedAsync(root.Run.SessionId.Value, HarnessStoreTestFixture.Timestamp, default);
        await artifacts.CloseAndDeleteAsync(root.Run.SessionId.Value, HarnessStoreTestFixture.Timestamp, default);
        command.CommandText = "SELECT count(*) FROM agent_subagent_change_chunks";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
