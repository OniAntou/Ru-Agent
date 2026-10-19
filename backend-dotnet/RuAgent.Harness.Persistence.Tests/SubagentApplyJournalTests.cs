using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentApplyJournalTests
{
    [Fact]
    public async Task Restart_retains_pending_intent_encrypted_and_rejects_result_substitution()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var store = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        await store.CreateAsync(new("agent_one", root.Run.RunId, "spawn_1", SubagentRole.Coding, fixture.NewRun(2, 2, 1)), default);
        var changes = new SubagentChangeSet("agent_one", root.Run.RunId.Value, fixture.RootPath, "snapshot_one", 1,
            [new("private-name.txt", new string('a', 64), new string('b', 64), new byte[] { 1 }, 100644)]);
        var crypto = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var journal = new SqliteSubagentApplyJournal(fixture.Options, crypto);
        Assert.Null(await journal.BeginAsync(changes, new string('a', 64), default));
        await journal.RecordFileIntentAsync(changes, 0, default);
        var restarted = new SqliteSubagentApplyJournal(fixture.Options, crypto);
        Assert.Equal(SubagentApplyStatus.Pending, (await restarted.BeginAsync(changes, new string('a', 64), default))!.Status);
        await Assert.ThrowsAsync<HarnessStoreException>(() => restarted.BeginAsync(changes, new string('c', 64), default));
        await restarted.FinishAsync(changes, new(SubagentApplyStatus.Unknown, 0, "subagent_apply_reconciliation_required"), default);
        Assert.Equal(SubagentApplyStatus.Unknown, (await journal.BeginAsync(changes, new string('a', 64), default))!.Status);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT ciphertext FROM agent_subagent_apply_files";
        Assert.DoesNotContain("private-name.txt", System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(await query.ExecuteScalarAsync())));
    }
}
