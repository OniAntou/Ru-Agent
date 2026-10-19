using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentApplyJournalReadTests
{
    [Theory]
    [InlineData(SubagentApplyStatus.Pending)]
    [InlineData(SubagentApplyStatus.Unknown)]
    [InlineData(SubagentApplyStatus.Partial)]
    [InlineData(SubagentApplyStatus.Applied)]
    public async Task Integration_bases_use_only_prior_durable_receipts_and_block_unresolved_operations(SubagentApplyStatus status)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Coding, fixture.NewRun(2, 2, 1)), default);
        var journal = new SqliteSubagentApplyJournal(fixture.Options, new WorkspaceJournalProtector(new FakeHarnessKeyStore()));
        var changes = new SubagentChangeSet("agent_one", root.Run.RunId.Value, fixture.RootPath, "snapshot_one", 1,
            [new("a.txt", new string('a', 64), new string('b', 64), new byte[] { 1 }, 100644),
             new("b.txt", new string('a', 64), "missing", ReadOnlyMemory<byte>.Empty, 100644)]);
        await journal.BeginAsync(changes, new string('a', 64), default);
        await journal.RecordFileIntentAsync(changes, 0, default);
        await journal.RecordFileReceiptAsync(changes, 0, default);
        await journal.RecordFileIntentAsync(changes, 1, default);
        if (status == SubagentApplyStatus.Applied) await journal.RecordFileReceiptAsync(changes, 1, default);
        if (status != SubagentApplyStatus.Pending)
            await journal.FinishAsync(changes, new(status, status == SubagentApplyStatus.Applied ? 2 : 1, "saved"), default);
        Assert.Empty(await journal.ReadAppliedBaseHashesAsync(root.Run.RunId.Value, "agent_one", 1, default));
        if (status is SubagentApplyStatus.Pending or SubagentApplyStatus.Unknown)
        {
            var error = await Assert.ThrowsAsync<HarnessStoreException>(() => journal.ReadAppliedBaseHashesAsync(root.Run.RunId.Value, "agent_one", 2, default));
            Assert.Equal("subagent_apply_reconciliation_required", error.Code);
            return;
        }
        var bases = await journal.ReadAppliedBaseHashesAsync(root.Run.RunId.Value, "agent_one", 2, default);
        Assert.Equal(new string('b', 64), bases["a.txt"]);
        if (status == SubagentApplyStatus.Applied) Assert.Equal("missing", bases["b.txt"]);
        else Assert.False(bases.ContainsKey("b.txt"));
        if (status == SubagentApplyStatus.Applied)
        {
            var next = changes with { ResultVersion = 2, Files = [new("a.txt", new string('b', 64), new string('c', 64), new byte[] { 2 }, 100644)] };
            await journal.BeginAsync(next, new string('b', 64), default);
            await journal.RecordFileIntentAsync(next, 0, default);
            await journal.RecordFileReceiptAsync(next, 0, default);
            await journal.FinishAsync(next, new(SubagentApplyStatus.Applied, 1, "saved"), default);
            var newer = await journal.ReadAppliedBaseHashesAsync(root.Run.RunId.Value, "agent_one", 3, default);
            Assert.Equal(new string('c', 64), newer["a.txt"]);
            Assert.Equal("missing", newer["b.txt"]);
            Assert.Equal(new string('b', 64), (await journal.ReadAppliedBaseHashesAsync(root.Run.RunId.Value, "agent_one", 2, default))["a.txt"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_reads_exact_encrypted_intents_and_receipts_and_rejects_tampering(bool tamper)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Coding, fixture.NewRun(2, 2, 1)), default);
        var changes = new SubagentChangeSet("agent_one", root.Run.RunId.Value, fixture.RootPath, "snapshot_one", 1,
            [new("private-a.txt", new string('a', 64), new string('b', 64), new byte[] { 1 }, 100644),
             new("private-b.txt", new string('c', 64), new string('d', 64), new byte[] { 2 }, 100644)]);
        var crypto = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var journal = new SqliteSubagentApplyJournal(fixture.Options, crypto);
        Assert.Null(await journal.ReadAsync(root.Run.RunId.Value, "agent_one", 1, default));
        await journal.BeginAsync(changes, new string('a', 64), default);
        await journal.RecordFileIntentAsync(changes, 0, default);
        await journal.RecordFileReceiptAsync(changes, 0, default);
        await journal.RecordFileIntentAsync(changes, 1, default);
        var restarted = new SqliteSubagentApplyJournal(fixture.Options, crypto);
        if (tamper)
        {
            await using var connection = fixture.CreateRawConnection();
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_subagent_apply_files SET ciphertext=zeroblob(length(ciphertext)) WHERE file_index=1";
            await command.ExecuteNonQueryAsync();
            var error = await Assert.ThrowsAsync<HarnessStoreException>(() => restarted.ReadAsync(root.Run.RunId.Value, "agent_one", 1, default));
            Assert.Equal("journal_authentication_failed", error.Code);
            return;
        }
        var read = (await restarted.ReadAsync(root.Run.RunId.Value, "agent_one", 1, default))!;
        Assert.Equal(new string('a', 64), read.Fingerprint);
        Assert.Equal(SubagentApplyStatus.Pending, read.Outcome.Status);
        Assert.Equal(2, read.Files.Count);
        Assert.True(read.Files[0].Receipted);
        Assert.False(read.Files[1].Receipted);
        Assert.Equal("private-a.txt", read.Files[0].RelativePath);
        Assert.Equal(new string('c', 64), read.Files[1].BaseHash);
        await restarted.FinishAsync(changes, new(SubagentApplyStatus.Unknown, 1, "subagent_apply_reconciliation_required"), default);
        Assert.Equal(SubagentApplyStatus.Unknown, (await journal.ReadAsync(root.Run.RunId.Value, "agent_one", 1, default))!.Outcome.Status);
        await Assert.ThrowsAsync<HarnessStoreException>(() => restarted.ReadAsync(HarnessStoreTestFixture.Run(3).Value, "agent_one", 1, default));
    }
}
