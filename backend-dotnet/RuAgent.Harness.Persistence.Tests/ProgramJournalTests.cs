using System.Text.Json.Nodes;
using RuAgent.Protocol.Programs;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class ProgramJournalTests
{
    [Fact]
    public async Task JournalIsEncryptedRedactedInsertOnceAndDoesNotAdvanceRunVersion()
    {
        const string secret = "synthetic-program-secret-12345678";
        await using var fixture = await HarnessStoreTestFixture.CreateAsync([secret]);
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        var events = fixture.CreateEventStore();
        await events.TryCreateRunAsync(root, default);
        await SetRunningAsync(fixture);
        var before = await events.GetRunAsync(root.Run.RunId, default);
        IProgramJournal journal = fixture.PayloadStore;
        var created = await journal.CreateAsync(root.Run.RunId.Value, "program_test_header", new JsonObject { ["value"] = secret }, default);
        Assert.True(created.Created);
        Assert.DoesNotContain(secret, created.Value.ToJsonString(), StringComparison.Ordinal);
        var replay = await journal.CreateAsync(root.Run.RunId.Value, "program_test_header", new JsonObject { ["value"] = "replacement" }, default);
        Assert.False(replay.Created);
        Assert.True(JsonNode.DeepEquals(created.Value, replay.Value));
        Assert.True(JsonNode.DeepEquals(created.Value, await journal.ReadAsync(root.Run.RunId.Value, "program_test_header", default)));
        Assert.Equal(before!.Version, (await events.GetRunAsync(root.Run.RunId, default))!.Version);
        Assert.Null(await journal.ReadAsync(root.Run.RunId.Value, "program_missing", default));
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_content FROM agent_payloads WHERE kind=3";
        var bytes = (byte[])(await command.ExecuteScalarAsync())!;
        Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedCallsShareWindowBudgetAndReservationIsIdempotent()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        await SetRunningAsync(fixture, 255);
        IProgramJournal journal = fixture.PayloadStore;
        Assert.True(await journal.ReserveToolAsync(root.Run.RunId.Value, "program_one", default));
        Assert.True(await journal.ReserveToolAsync(root.Run.RunId.Value, "program_one", default));
        Assert.False(await journal.ReserveToolAsync(root.Run.RunId.Value, "program_two", default));
        var agents = new RuAgent.Harness.Persistence.Subagents.SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        Assert.True(await agents.HasFamilyAsync(root.Run.RunId, default));
    }

    [Fact]
    public async Task BatchReservationIsAtomicAndIdempotentAgainstSharedFamilyBudget()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        await SetRunningAsync(fixture, tools: 250);
        IProgramJournal journal = fixture.PayloadStore;

        Assert.False(await journal.ReserveToolsAsync(root.Run.RunId.Value, "program_batch", 8, default));
        Assert.True(await journal.ReserveToolsAsync(root.Run.RunId.Value, "program_batch", 6, default));
        Assert.True(await journal.ReserveToolsAsync(root.Run.RunId.Value, "program_batch", 6, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => journal.ReserveToolsAsync(
            root.Run.RunId.Value, "program_batch", 5, default));
    }

    private static async Task SetRunningAsync(HarnessStoreTestFixture fixture, int tools = 0)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_runs SET state=3,tool_calls_in_window=$tools";
        command.Parameters.AddWithValue("$tools", tools);
        await command.ExecuteNonQueryAsync();
    }
}
