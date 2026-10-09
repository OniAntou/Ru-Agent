using System.Net;
using System.Text.Json.Nodes;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

/// <summary>
/// Reasoning effort was unselectable for every Models.dev provider.
/// </summary>
/// <remarks>
/// OpenRouter's own <c>GET /api/v1/models</c> returns bare ids: no
/// supported_parameters, no reasoning_efforts. ProviderCatalogParser therefore
/// left every model at reasoning state "unknown" with no options, and the desktop
/// picker only offers levels or a toggle, so the dropdown collapsed to a single
/// "default" entry and there was nothing to select.
///
/// Models.dev does publish the metadata, as reasoning_options, and that is the
/// provider's own declaration rather than anything inferred from a model name.
/// The fix reads it and fills in only the models the provider left at "unknown".
///
/// What it deliberately does not do: a toggle-only entry, an empty list
/// (models.dev publishes that for deepseek-r1) and a missing field all stay
/// "unknown" with no options, so nothing undeclared is ever offered. Values are
/// filtered to the seven ProviderRequestOptionsValidator accepts, so the picker
/// cannot present an effort the runtime would reject.
/// </remarks>
public sealed class ModelsDevReasoningCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-reasoning-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly Credentials _credentials = new();
    private readonly Handler _handler = new();

    private const string ModelsDevPrefix = "models.dev:";
    private const string OpenRouterApi = "https://openrouter.ai/api/v1";

    /// <summary>
    /// Enough providers to clear MinimumModelsDevProviders, plus the one under test
    /// carrying the given model entries.
    /// </summary>
    private static string ModelsDevPayload(params (string Id, string Json)[] models) =>
        "{" + string.Join(",", Enumerable.Range(0, 60)
            .Select(i => $"\"prov{i:000}\":{{\"api\":\"https://prov{i:000}.example/v1\"}}")
            .Append(
                "\"openrouter\":{\"api\":\"" + OpenRouterApi + "\",\"models\":{" +
                string.Join(",", models.Select(m => $"\"{m.Id}\":{m.Json}")) + "}}")) + "}";

    private async Task WriteModelsDevCacheAsync(string providersJson)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "models-dev.json"),
            $"{{\"fetched_at\":\"2099-01-01T00:00:00+00:00\",\"payload\":{providersJson}}}");
    }

    /// <summary>
    /// Runs the real path: the provider's own /models is served by the stub, the
    /// models.dev catalog comes from the cache file.
    /// </summary>
    private async Task<IReadOnlyList<CatalogModel>> CatalogForAsync(
        string providerModelsJson,
        string modelsDevPayload)
    {
        var snapshot = await SnapshotForAsync(providerModelsJson, modelsDevPayload);
        return snapshot.Catalog;
    }

    /// <summary>
    /// The whole snapshot, for the tests that need Identity or SelectedModel to
    /// match what the save path compares against.
    /// </summary>
    private async Task<ProviderCatalogSnapshot> SnapshotForAsync(
        string providerModelsJson,
        string modelsDevPayload)
    {
        await WriteModelsDevCacheAsync(modelsDevPayload);
        _handler.Json = providerModelsJson;

        var service = new ProviderCatalogService(
            new HttpClient(_handler),
            _credentials,
            _clock,
            Path.Combine(_root, "models-dev.json"));
        var settings = await new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root))
            .ReadAsync(default);
        var snapshot = await service.GetAsync(
            (settings with { Backend = ModelsDevPrefix + "openrouter", Model = "vendor/model-a" }),
            false,
            CancellationToken.None);

        Assert.True(snapshot.Status == "ok", $"status={snapshot.Status} error={snapshot.ErrorCode}");
        return snapshot;
    }

    [Fact]
    public async Task A_published_effort_list_reaches_a_provider_that_omits_it()
    {
        var catalog = await CatalogForAsync(
            "{\"data\":[{\"id\":\"vendor/model-a\"}]}",
            ModelsDevPayload(("vendor/model-a",
                "{\"reasoning\":true,\"reasoning_options\":[{\"type\":\"effort\",\"values\":[\"low\",\"medium\",\"high\",\"xhigh\",\"max\"]}]}")));

        var entry = Assert.Single(catalog, item => item.Id == "vendor/model-a");
        Assert.Equal("levels", entry.Reasoning.State);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], entry.Reasoning.Options);
    }

    [Fact]
    public async Task A_toggle_only_entry_offers_no_levels()
    {
        var catalog = await CatalogForAsync(
            "{\"data\":[{\"id\":\"vendor/model-a\"}]}",
            ModelsDevPayload(("vendor/model-a",
                "{\"reasoning\":true,\"reasoning_options\":[{\"type\":\"toggle\"}]}")));

        var entry = Assert.Single(catalog, item => item.Id == "vendor/model-a");
        Assert.Equal("unknown", entry.Reasoning.State);
        Assert.Empty(entry.Reasoning.Options);
    }

    [Fact]
    public async Task An_empty_effort_list_offers_nothing()
    {
        var catalog = await CatalogForAsync(
            "{\"data\":[{\"id\":\"vendor/model-a\"}]}",
            ModelsDevPayload(("vendor/model-a",
                "{\"reasoning\":true,\"reasoning_options\":[]}")));

        var entry = Assert.Single(catalog, item => item.Id == "vendor/model-a");
        Assert.Equal("unknown", entry.Reasoning.State);
        Assert.Empty(entry.Reasoning.Options);
    }

    [Fact]
    public async Task A_model_absent_from_models_dev_stays_unknown()
    {
        var catalog = await CatalogForAsync(
            "{\"data\":[{\"id\":\"vendor/model-a\"},{\"id\":\"vendor/model-b\"}]}",
            ModelsDevPayload(("vendor/model-a",
                "{\"reasoning_options\":[{\"type\":\"effort\",\"values\":[\"high\"]}]}")));

        Assert.Equal("levels", Assert.Single(catalog, item => item.Id == "vendor/model-a").Reasoning.State);
        var untouched = Assert.Single(catalog, item => item.Id == "vendor/model-b");
        Assert.Equal("unknown", untouched.Reasoning.State);
        Assert.Empty(untouched.Reasoning.Options);
    }

    [Fact]
    public async Task A_level_the_provider_declared_itself_is_not_overwritten()
    {
        // The provider's own /models said low+high. models.dev is not consulted for
        // this model, so its list cannot widen or narrow the provider's own answer.
        var catalog = await CatalogForAsync(
            "{\"data\":[{\"id\":\"vendor/model-a\",\"supported_parameters\":[\"reasoning_effort\"],\"reasoning_efforts\":[\"low\",\"high\"]}]}",
            ModelsDevPayload(("vendor/model-a",
                "{\"reasoning_options\":[{\"type\":\"effort\",\"values\":[\"low\",\"medium\",\"high\",\"xhigh\",\"max\"]}]}")));

        var entry = Assert.Single(catalog, item => item.Id == "vendor/model-a");
        Assert.Equal(["low", "high"], entry.Reasoning.Options);
    }

    [Fact]
    public async Task Effort_values_outside_the_worker_allowlist_are_dropped()
    {
        var catalog = await CatalogForAsync(
            "{\"data\":[{\"id\":\"vendor/model-a\"}]}",
            ModelsDevPayload(("vendor/model-a",
                "{\"reasoning_options\":[{\"type\":\"effort\",\"values\":[\"low\",\"turbo\",\"medium\",\"ULTRA\",\"high\"]}]}")));

        var entry = Assert.Single(catalog, item => item.Id == "vendor/model-a");
        Assert.Equal(["low", "medium", "high"], entry.Reasoning.Options);
    }

    [Fact]
    public async Task A_control_character_or_overlong_level_is_dropped_and_duplicates_collapse()
    {
        var catalog = await CatalogForAsync(
            "{\"data\":[{\"id\":\"vendor/model-a\"}]}",
            ModelsDevPayload(("vendor/model-a",
                "{\"reasoning_options\":[{\"type\":\"effort\",\"values\":[\"high\",\"low\",\"high\",\"me\\u0001dium\"]}]}")));

        var entry = Assert.Single(catalog, item => item.Id == "vendor/model-a");
        Assert.Equal(["high", "low"], entry.Reasoning.Options);
    }

    [Fact]
    public async Task Reasoning_options_of_the_wrong_shape_are_ignored()
    {
        var catalog = await CatalogForAsync(
            "{\"data\":[{\"id\":\"vendor/model-a\"}]}",
            ModelsDevPayload(("vendor/model-a", "{\"reasoning_options\":\"not-an-array\"}")));

        var entry = Assert.Single(catalog, item => item.Id == "vendor/model-a");
        Assert.Equal("unknown", entry.Reasoning.State);
        Assert.Empty(entry.Reasoning.Options);
    }

    [Fact]
    public async Task A_custom_endpoint_provider_is_never_given_models_dev_levels()
    {
        // The merge is scoped to models.dev backends. A custom OpenAI-compatible
        // server is not in the registry, so it keeps the bare-id behaviour.
        await WriteModelsDevCacheAsync(ModelsDevPayload(
            ("vendor/model-a", "{\"reasoning_options\":[{\"type\":\"effort\",\"values\":[\"high\"]}]}")));
        _handler.Json = "{\"data\":[{\"id\":\"vendor/model-a\"}]}";

        var service = new ProviderCatalogService(
            new HttpClient(_handler), _credentials, _clock, Path.Combine(_root, "models-dev.json"));
        var settings = await new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root))
            .ReadAsync(default);
        var snapshot = await service.GetAsync(
            (settings with { Backend = "Custom OpenAI", CustomUrl = "http://127.0.0.1:9/v1", Model = "vendor/model-a" }),
            false,
            CancellationToken.None);

        var entry = Assert.Single(snapshot.Catalog, item => item.Id == "vendor/model-a");
        Assert.Equal("unknown", entry.Reasoning.State);
    }

    [Fact]
    public async Task A_published_level_is_accepted_by_the_save_path()
    {
        // The dropdown renders from CatalogModel.Reasoning, but the store and the
        // bootstrap snapshot both go through ProviderReasoningPreferences.Resolve,
        // which reads CatalogModel.WireOptions. Filling only Reasoning made the level
        // visible and then rejected the moment it was picked: the save answered
        // "Invalid settings payload" and the button snapped back to "default".
        var snapshot = await SnapshotForAsync(
            "{\"data\":[{\"id\":\"vendor/model-a\"}]}",
            ModelsDevPayload(("vendor/model-a",
                "{\"reasoning_options\":[{\"type\":\"effort\",\"values\":[\"low\",\"high\"]}]}")));

        var wire = ProviderReasoningPreferences.Resolve(snapshot, "vendor/model-a", "high");
        Assert.Equal("high", wire["reasoning_effort"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_published_level_that_was_never_declared_is_still_rejected()
    {
        // Resolve stays a closed gate: only the levels the provider published are
        // accepted, so a stale or hand-written settings payload cannot smuggle in a
        // level the runtime would reject.
        var snapshot = await SnapshotForAsync(
            "{\"data\":[{\"id\":\"vendor/model-a\"}]}",
            ModelsDevPayload(("vendor/model-a",
                "{\"reasoning_options\":[{\"type\":\"effort\",\"values\":[\"high\"]}]}")));

        var error = Assert.Throws<ProviderCatalogException>(() =>
            ProviderReasoningPreferences.Resolve(snapshot, "vendor/model-a", "low"));

        Assert.Equal("reasoning_unsupported", error.Code);
    }

    [Fact]
    public async Task A_rewritten_cache_file_is_picked_up_without_a_restart()
    {
        // The parsed list is held in memory keyed by size+mtime; a rewrite must
        // invalidate it, or republished levels never appear until restart. The
        // two payloads differ in length so the key change is deterministic,
        // not dependent on timestamp granularity. GetModelsDevAsync has no
        // fetch throttle, so this exercises the invalidation directly.
        await WriteModelsDevCacheAsync(ModelsDevPayload(("vendor/model-a",
            "{\"reasoning_options\":[{\"type\":\"effort\",\"values\":[\"low\"]}]}")));
        var service = new ProviderCatalogService(
            new HttpClient(_handler),
            _credentials,
            _clock,
            Path.Combine(_root, "models-dev.json"));

        var first = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal(["low"], LevelsOf(first, "vendor/model-a"));

        await WriteModelsDevCacheAsync(ModelsDevPayload(("vendor/model-a",
            "{\"reasoning_options\":[{\"type\":\"effort\",\"values\":[\"low\",\"high\"]}]}")));
        var second = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal(["low", "high"], LevelsOf(second, "vendor/model-a"));
    }

    private static IReadOnlyList<string> LevelsOf(
        IReadOnlyList<ModelsDevProvider> providers,
        string id)
    {
        var provider = Assert.Single(providers, item => item.Id == "openrouter");
        Assert.NotNull(provider.Reasoning);
        Assert.True(provider.Reasoning.TryGetValue(id, out var published));
        return published.Options;
    }

    public void Dispose()
    {
        SqliteTestPools.ClearHarnessDatabase(DataPaths.Resolve(_root, _root, _root, _root).AgentHarnessDatabase);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Credentials : ICredentialStore
    {
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("synthetic-key-1");

        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Json = "{\"data\":[]}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json) });
    }
}
