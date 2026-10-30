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

    [Fact]
    public async Task Migration_from_31_preserves_existing_runs_and_leaves_them_without_an_intent()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync(); await fixture.RegisterWorkspaceAsync(1);
        var run = fixture.NewRun(1, 1, 1, 'a', 'b');
        await fixture.CreateEventStore().TryCreateRunAsync(run, default);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE agent_run_provider_intents; PRAGMA user_version=31;";
            await command.ExecuteNonQueryAsync();
        }
        Assert.True((await HarnessSqliteSchema.InitializeAsync(fixture.Options, default)).IsAvailable);
        Assert.NotNull(await fixture.CreateEventStore().GetRunAsync(run.Run.RunId, default));
        Assert.Null(await new SqliteAgentRunProviderSelectionStore(fixture.Options).GetIntentAsync(run.Run.RunId, default));
    }
}
