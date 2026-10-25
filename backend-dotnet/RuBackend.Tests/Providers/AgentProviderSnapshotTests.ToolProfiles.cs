using RuAgent.Harness.Persistence;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using RuBackend.Tests.Harness;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed partial class AgentProviderSnapshotTests
{
    [Theory]
    [InlineData("{}", "tool_profile_required")]
    [InlineData("{\"ru_tool_profile\":\"legacy_v1\"}", "tool_profile_invalid")]
    public async Task Retired_or_unversioned_pins_fail_before_provider_access(string options, string code)
    {
        var settings = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);
        await selections.PinAsync(run.Run.RunId,
            CustomPin("https://provider.invalid/v1", "a") with { RequestOptionsJson = options }, default);
        var handler = new UnexpectedProfileProviderHandler();
        var credentials = new Credentials();
        await using var factory = new AgentProviderSnapshotFactory(settings, credentials,
            new ProviderCatalogService(new HttpClient(handler), credentials, TimeProvider.System),
            selections, (_, _) => Task.FromResult(false));

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() =>
            new AgentRunModelMetadataResolver(factory, selections).ResolveAsync(run.Run.RunId, default));

        Assert.Equal(code, error.Code);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(options, (await selections.GetAsync(run.Run.RunId, default))!.Selection!.RequestOptionsJson);
    }

    private sealed class UnexpectedProfileProviderHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Retired pins must not contact a provider.");
        }
    }
}
