using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class ProviderIntentTests
{
    [Fact]
    public async Task Intent_is_committed_with_the_run_replayed_unchanged_and_rejects_another_pin()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var source = fixture.NewRun(1, 1, 1, 'a', 'b');
        var request = new NewAgentRun(source.Run, source.Events, source.Payloads, source.ConversationOutbox, source.CreationIdentity,
            new RunProviderIntent("Custom OpenAI", "old-model"));
        var store = fixture.CreateEventStore();
        Assert.Equal(CreateRunOutcome.Created, (await store.TryCreateRunAsync(request, default)).Outcome);
        var selections = new SqliteAgentRunProviderSelectionStore(fixture.Options);
        Assert.Equal(request.ProviderIntent, await selections.GetIntentAsync(source.Run.RunId, default));
        Assert.Equal(CreateRunOutcome.AlreadyExists, (await store.TryCreateRunAsync(request, default)).Outcome);
        var error = await Assert.ThrowsAsync<HarnessStoreException>(() => selections.PinAsync(source.Run.RunId,
            new("Custom OpenAI", "custom", "chat_completions", "https://provider.invalid/v1", "other-model", "custom_api_key", "{}", null,
                ModelLabel: "other-model", CallingMode: "native"), default));
        Assert.Equal("provider_intent_mismatch", error.Code);
        Assert.Null((await selections.GetAsync(source.Run.RunId, default))!.Selection);
    }

    [Theory]
    [InlineData(31, false)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public async Task Migration_preserves_runs_intents_and_execution_index(int version, bool intentAlreadyPresent)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var run = fixture.NewRun(1, 1, 1, 'a', 'b');
        var intent = intentAlreadyPresent ? new RunProviderIntent("Custom OpenAI", "retained-model") : null;
        var request = new NewAgentRun(run.Run, run.Events, run.Payloads, run.ConversationOutbox, run.CreationIdentity, intent);
        await fixture.CreateEventStore().TryCreateRunAsync(request, default);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = (intentAlreadyPresent ? "" : "DROP TABLE agent_run_provider_intents; ")
                + (version == 31 || intentAlreadyPresent ? "DROP INDEX ix_agent_events_execution_request; " : "")
                + $"PRAGMA user_version={version};";
            await command.ExecuteNonQueryAsync();
        }
        await using (var pooled = await fixture.Options.OpenConnectionAsync(default))
            SqliteConnection.ClearPool(pooled);
        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, default);
        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='ix_agent_events_execution_request'";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        Assert.NotNull(await fixture.CreateEventStore().GetRunAsync(run.Run.RunId, default));
        Assert.Equal(intent, await new SqliteAgentRunProviderSelectionStore(fixture.Options).GetIntentAsync(run.Run.RunId, default));
    }
}
