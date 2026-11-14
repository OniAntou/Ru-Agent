using System.Text.Json.Nodes;
using RuAgent.Harness.Persistence;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using RuBackend.Tests.Harness;
using Xunit;

namespace RuBackend.Tests.Providers;

/// <summary>
/// ADR 0047: the advisor is resolved once when a new root run pins its selection, and
/// its marker never reaches the provider's request options.
/// </summary>
public sealed partial class AgentProviderSnapshotTests
{
    [Fact]
    public async Task Advisor_is_pinned_with_the_selection_and_stripped_from_wire_options()
    {
        var (pinned, bootstrap) = await PinWithAdvisorAsync("b");

        var advisor = AdvisorPolicy.ReadPinned(pinned);
        Assert.NotNull(advisor?.Selection);
        Assert.Null(advisor!.UnavailableCode);
        Assert.Equal("b", advisor.Selection!.Model);
        Assert.Equal("Custom OpenAI", advisor.Selection.Backend);
        // The advisor's own options carry no Ru markers, and no credential is stored.
        Assert.DoesNotContain("_ru_", advisor.Selection.RequestOptionsJson);
        Assert.DoesNotContain("key-one", pinned!.RequestOptionsJson);
        Assert.False(bootstrap.RequestOptions?.ContainsKey(AdvisorPolicy.PinField) ?? false);
    }

    [Fact]
    public async Task An_advisor_that_cannot_be_resolved_is_pinned_as_unavailable_without_substitution()
    {
        var (pinned, _) = await PinWithAdvisorAsync("not-in-catalog");

        var advisor = AdvisorPolicy.ReadPinned(pinned);
        Assert.Null(advisor!.Selection);
        Assert.Equal("advisor_unavailable", advisor.UnavailableCode);
        Assert.Equal("a", pinned!.Model);
    }

    [Fact]
    public async Task A_run_pinned_without_an_advisor_setting_records_that_none_was_selected()
    {
        var (pinned, bootstrap) = await PinWithAdvisorAsync(null);

        var advisor = AdvisorPolicy.ReadPinned(pinned);
        Assert.NotNull(advisor);
        Assert.False(advisor!.IsSelected);
        Assert.False(bootstrap.RequestOptions?.ContainsKey(AdvisorPolicy.PinField) ?? false);
    }

    [Fact]
    public async Task An_advisor_on_a_provider_without_its_key_is_pinned_as_unavailable()
    {
        var (pinned, _) = await PinWithAdvisorAsync("claude-opus-5-5", "Anthropic", new EmptyCredentials());

        var advisor = AdvisorPolicy.ReadPinned(pinned);
        Assert.Null(advisor!.Selection);
        Assert.Equal("advisor_unavailable", advisor.UnavailableCode);
    }

    [Fact]
    public async Task Resolver_uses_the_pinned_advisor_and_names_why_it_cannot()
    {
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var (resolver, selections, runId, factory) = await ResolverAsync(harness);
        await using var _ = factory;
        var marker = AdvisorPolicy.Marker(CustomPin("https://provider.invalid/v1", "b"));
        await selections.PinAsync(runId, CustomPin("https://provider.invalid/v1", "a") with
        {
            RequestOptionsJson = new JsonObject { ["ru_tool_profile"] = "coding_v2", [AdvisorPolicy.PinField] = marker }.ToJsonString(),
        }, default);

        var target = await resolver.ResolveAsync(runId, null, null, default);

        Assert.Equal("b", target.Model);
        Assert.Equal("b", target.Bootstrap.Model);
        Assert.False(target.Bootstrap.RequestOptions?.ContainsKey("ru_tool_profile") ?? false);
        // An effort the user's prompt does not name is refused before any catalog call.
        var refused = await Assert.ThrowsAsync<AdvisorResolutionException>(() => resolver.ResolveAsync(runId, null, "high", default));
        Assert.Equal(RuAgent.Harness.Core.AdvisorCodes.EffortUserRequestRequired, refused.Code);
    }

    [Theory]
    [InlineData(false, "advisor_not_selected")]
    [InlineData(true, "advisor_unavailable")]
    public async Task Resolver_reports_a_run_without_a_usable_advisor_by_name(bool unavailable, string code)
    {
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var (resolver, selections, runId, factory) = await ResolverAsync(harness);
        await using var _ = factory;
        var marker = unavailable ? AdvisorPolicy.UnavailableMarker() : AdvisorPolicy.NotSelectedMarker();
        await selections.PinAsync(runId, CustomPin("https://provider.invalid/v1", "a") with
        {
            RequestOptionsJson = new JsonObject { ["ru_tool_profile"] = "coding_v2", [AdvisorPolicy.PinField] = marker }.ToJsonString(),
        }, default);

        var error = await Assert.ThrowsAsync<AdvisorResolutionException>(() => resolver.ResolveAsync(runId, null, null, default));
        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData("selected", true)]
    [InlineData("unavailable", false)]
    [InlineData("not_selected", false)]
    public async Task Only_a_resolved_advisor_selection_is_recommended_while_every_marker_advertises_the_tool(
        string pin, bool recommended)
    {
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var (_, selections, runId, factory) = await ResolverAsync(harness);
        await using var _ = factory;
        var marker = pin switch
        {
            "selected" => AdvisorPolicy.Marker(CustomPin("https://provider.invalid/v1", "b")),
            "unavailable" => AdvisorPolicy.UnavailableMarker(),
            _ => AdvisorPolicy.NotSelectedMarker(),
        };
        await selections.PinAsync(runId, CustomPin("https://provider.invalid/v1", "a") with
        {
            RequestOptionsJson = new JsonObject { ["ru_tool_profile"] = "coding_v2", [AdvisorPolicy.PinField] = marker }.ToJsonString(),
        }, default);

        var metadata = await new AgentRunModelMetadataResolver(factory, selections).ResolveAsync(runId, default);

        Assert.True(metadata.Advisor);
        Assert.Equal(recommended, metadata.AdvisorSelected);
    }

    private async Task<(AdvisorTargetResolver Resolver, SqliteAgentRunProviderSelectionStore Selections,
        RuAgent.Harness.Core.RunId RunId, AgentProviderSnapshotFactory Factory)> ResolverAsync(BackendHarnessTestFixture harness)
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "Custom OpenAI", CustomModel = "a", CustomUrl = "https://provider.invalid/v1" }, default);
        var run = await harness.SeedRunAsync();
        var credentials = new Credentials();
        var catalog = new ProviderCatalogService(new HttpClient(new Handler()), credentials, TimeProvider.System);
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);
        var factory = new AgentProviderSnapshotFactory(store, credentials, catalog, selections, (_, _) => Task.FromResult(false));
        return (new AdvisorTargetResolver(selections, factory, new NoRequestedModels(), harness.PayloadStore), selections, run.Run.RunId, factory);
    }

    private sealed class NoRequestedModels : RuBackend.Harness.Subagents.ISubagentModelSelectionProvider
    {
        public Task<AgentRunProviderSelection> ResolveAsync(RuAgent.Harness.Core.RunId rootRunId,
            RuAgent.Protocol.Subagents.SubagentRole role, CancellationToken ct, string? model = null, string? providerId = null) =>
            throw new InvalidOperationException("no user-named model in these tests");
    }

    private async Task<(AgentRunProviderSelection? Pinned, RuAgent.Runtime.Adapters.Bootstrap.ProviderBootstrapData Bootstrap)>
        PinWithAdvisorAsync(string? advisorModel, string advisorBackend = "Custom OpenAI", ICredentialStore? credentialStore = null)
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        var current = await store.ReadAsync(default);
        var raw = current.Raw.DeepClone().AsObject();
        if (advisorModel is not null)
        {
            raw[AdvisorPolicy.BackendField] = advisorBackend;
            raw[AdvisorPolicy.ModelField] = advisorModel;
        }

        await store.SaveAsync(current with
        { Raw = raw, Backend = "Custom OpenAI", CustomModel = "a", CustomUrl = "https://provider.invalid/v1" }, default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var credentials = credentialStore ?? new Credentials();
        var catalog = new ProviderCatalogService(new HttpClient(new Handler()), credentials, TimeProvider.System);
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);
        await using var factory = new AgentProviderSnapshotFactory(
            store, credentials, catalog, selections, (_, _) => Task.FromResult(false));

        var bootstrap = await factory.GetAsync(run.Run.RunId, default);
        return ((await selections.GetAsync(run.Run.RunId, default))!.Selection, bootstrap);
    }
}
