using System.Text.Json.Nodes;
using RuAgent.Harness.Persistence;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using RuBackend.Tests.Harness;
using Xunit;

namespace RuBackend.Tests.Providers;

/// <summary>
/// ADR 0026: whether the run's model accepts image input is pinned with the provider
/// selection, and the marker never reaches the provider's request options.
/// </summary>
public sealed partial class AgentProviderSnapshotTests
{
    [Theory]
    [InlineData("a", false)]
    [InlineData("gpt-5-mini", true)]
    public async Task Image_input_is_pinned_with_the_selection_and_stripped_from_wire_options(string model, bool accepts)
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "Custom OpenAI", CustomModel = model, CustomUrl = "https://provider.invalid/v1" }, default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var credentials = new Credentials();
        var catalog = new ProviderCatalogService(new HttpClient(new VisionCatalogHandler()), credentials, TimeProvider.System);
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);
        await using var factory = new AgentProviderSnapshotFactory(
            store, credentials, catalog, selections, (_, _) => Task.FromResult(false));

        var bootstrap = await factory.GetAsync(run.Run.RunId, default);
        var pinned = (await selections.GetAsync(run.Run.RunId, default))!.Selection;

        Assert.Equal(accepts, ImageInputPolicy.IsPinned(pinned));
        Assert.False(bootstrap.RequestOptions?.ContainsKey(ImageInputPolicy.PinField) ?? false);

        // A later settings change does not move the run's decision.
        await store.SaveAsync((await store.ReadAsync(default)) with { CustomModel = accepts ? "a" : "gpt-5-mini" }, default);
        Assert.Equal(accepts, ImageInputPolicy.IsPinned((await selections.GetAsync(run.Run.RunId, default))!.Selection));
    }

    [Fact]
    public void Image_input_marker_is_consumed_and_a_malformed_marker_fails_closed()
    {
        var options = new JsonObject { [ImageInputPolicy.PinField] = true, ["temperature"] = 0.2 };
        Assert.True(ImageInputPolicy.TakePinnedCapability(options));
        Assert.False(options.ContainsKey(ImageInputPolicy.PinField));
        Assert.False(ImageInputPolicy.TakePinnedCapability(new JsonObject()));
        Assert.Throws<ProviderCatalogException>(() =>
            ImageInputPolicy.TakePinnedCapability(new JsonObject { [ImageInputPolicy.PinField] = "yes" }));
        Assert.False(ImageInputPolicy.IsPinned(null));
    }

    private sealed class VisionCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"a"},{"id":"gpt-5-mini"}]}"""),
            });
    }
}
