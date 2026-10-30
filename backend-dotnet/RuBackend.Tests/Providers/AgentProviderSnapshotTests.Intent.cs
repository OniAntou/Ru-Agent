using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using RuBackend.Tests.Harness;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed partial class AgentProviderSnapshotTests
{
    [Fact]
    public async Task Unresolved_run_intent_survives_settings_changes_and_factory_restart()
    {
        var settings = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await settings.SaveAsync((await settings.ReadAsync(default)) with
            { Backend = "Anthropic", AnthropicModel = "changed-model" }, default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        await using (var connection = new SqliteConnection($"Data Source={harness.Options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO agent_run_provider_intents(run_id,backend,model) VALUES($run,'OpenAI','gpt-5.6-sol')";
            command.Parameters.AddWithValue("$run", run.Run.RunId.Value); await command.ExecuteNonQueryAsync();
        }
        var credentials = new TrackingCredentials();
        var catalog = new ProviderCatalogService(new HttpClient(new TrackingCatalogHandler()), credentials, TimeProvider.System);
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);
        await using (var first = new AgentProviderSnapshotFactory(settings, credentials, catalog, selections, (_, _) => Task.FromResult(false)))
            _ = await first.GetAsync(run.Run.RunId, default);
        var pin = (await selections.GetAsync(run.Run.RunId, default))!.Selection!;
        Assert.Equal("OpenAI", pin.Backend); Assert.Equal("gpt-5.6-sol", pin.Model);
        Assert.DoesNotContain("anthropic_api_key", credentials.RequestedAccounts);
        await using var restarted = new AgentProviderSnapshotFactory(settings, credentials, catalog, selections, (_, _) => Task.FromResult(false));
        _ = await restarted.GetAsync(run.Run.RunId, default);
        Assert.Equal(pin, (await selections.GetAsync(run.Run.RunId, default))!.Selection);
        Assert.Equal("Anthropic", (await settings.ReadAsync(default)).Backend);
    }
}
