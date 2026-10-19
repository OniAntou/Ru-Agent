using System.Text.Json;
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
    public async Task Describing_role_selection_does_not_pin_a_run_or_return_credentials()
    {
        var settings = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        var current = (await settings.ReadAsync(default)) with
        { Backend = "Custom OpenAI", CustomModel = "a", CustomUrl = "https://provider.invalid/v1" };
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var credentials = new Credentials();
        var catalog = new ProviderCatalogService(new HttpClient(new Handler()), credentials, TimeProvider.System);
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);
        using var factory = new AgentProviderSnapshotFactory(settings, credentials, catalog, selections, (_, _) => Task.FromResult(false));
        var described = await factory.DescribeSelectionAsync(current, default);
        Assert.Equal("a", described.Model);
        Assert.Equal("chat_completions", described.Protocol);
        Assert.DoesNotContain("key-one", JsonSerializer.Serialize(described));
        Assert.Null((await selections.GetAsync(run.Run.RunId, default))!.Selection);
    }
}
