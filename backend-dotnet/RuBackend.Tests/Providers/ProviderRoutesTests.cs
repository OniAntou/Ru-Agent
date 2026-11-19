using System.Net;
using RuAgent.Harness.Persistence;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using RuBackend.Tests.Harness;
using Xunit;

namespace RuBackend.Tests.Providers;

/// <summary>The Models.dev provider route table (ADR 0052).</summary>
public sealed class ProviderRoutesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-routes-test-" + Guid.NewGuid().ToString("N"));

    private const string Catalog = """
        {
          "minimax": {"npm": "@ai-sdk/anthropic", "api": "https://api.minimax.io/anthropic/v1",
            "models": {"MiniMax-M2": {"id": "MiniMax-M2", "limit": {"context": 204800, "output": 131072},
              "reasoning": true, "reasoning_efforts": ["low", "high"]},
              "MiniMax-VL": {"id": "MiniMax-VL", "attachment": true}}},
          "xai": {"npm": "@ai-sdk/xai", "models": {"grok-4": {"id": "grok-4"}}},
          "perplexity": {"npm": "@ai-sdk/perplexity", "models": {"sonar": {"id": "sonar"}}},
          "google": {"npm": "@ai-sdk/google", "models": {"gemini": {"id": "gemini"}}},
          "nowhere": {"npm": "@ai-sdk/somebody", "models": {"m": {"id": "m"}}},
          "openai": {"npm": "@ai-sdk/openai", "models": {"gpt": {"id": "gpt"}}},
          "anthropic": {"npm": "@ai-sdk/anthropic", "models": {"claude": {"id": "claude"}}},
          "amazon-bedrock": {"npm": "@ai-sdk/amazon-bedrock", "api": "https://bedrock.example/v1", "models": {}},
          "plain": {"npm": "@ai-sdk/openai-compatible", "api": "https://plain.example/v1", "models": {}},
          "legacy": {"api": "https://legacy.example/v1", "models": {}}
        }
        """;

    [Fact]
    public void The_protocol_follows_the_entrys_package_and_missing_roots_come_from_the_table()
    {
        var providers = ModelsDevCatalogParser.Parse(Catalog).ToDictionary(provider => provider.Id);

        Assert.Equal(ProviderProtocols.AnthropicMessages, providers["minimax"].Protocol);
        Assert.Equal("https://api.minimax.io/anthropic/v1", providers["minimax"].BaseUrl);
        Assert.Equal(ProviderProtocols.ChatCompletions, providers["xai"].Protocol);
        Assert.Equal("https://api.x.ai/v1", providers["xai"].BaseUrl);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai", providers["google"].BaseUrl);
        Assert.Equal(ProviderProtocols.ChatCompletions, providers["plain"].Protocol);
        // An entry without a package keeps the chat-completions route it always had.
        Assert.Equal(ProviderProtocols.ChatCompletions, providers["legacy"].Protocol);
        // No root anywhere, a non-key credential, or a first-party account: not offered.
        Assert.DoesNotContain("nowhere", providers.Keys);
        // Perplexity has no endpoint here: none of its models call tools.
        Assert.DoesNotContain("perplexity", providers.Keys);
        Assert.DoesNotContain("amazon-bedrock", providers.Keys);
        Assert.DoesNotContain("openai", providers.Keys);
        Assert.DoesNotContain("anthropic", providers.Keys);
    }

    [Fact]
    public void An_anthropic_gateway_is_called_at_messages()
    {
        var settings = Settings("models.dev:minimax", "MiniMax-M2");
        var selection = ProviderSelection.From(settings, ModelsDevCatalogParser.Parse(Catalog));
        Assert.Equal(ProviderProtocols.AnthropicMessages, selection.Protocol);
        Assert.Equal(new Uri("https://api.minimax.io/anthropic/v1/messages"), selection.CompletionUri);
    }

    [Fact]
    public async Task An_anthropic_gateways_models_come_from_models_dev_with_their_limits_and_no_effort_fields()
    {
        var catalog = new ProviderCatalogService(new HttpClient(new Refusing()), new Keyed(), TimeProvider.System, await CacheAsync());

        var snapshot = await catalog.GetAsync("models.dev:minimax", Settings("models.dev:minimax", "MiniMax-M2"), true, default);

        Assert.Equal("ok", snapshot.Status);
        var model = Assert.Single(snapshot.Catalog, entry => entry.Id == "MiniMax-M2");
        Assert.Equal(204800, model.ContextLength);
        // Effort levels become chat-completions fields; a Messages gateway gets none.
        Assert.Equal(CatalogReasoningStates.Unknown, model.Reasoning.State);
        // /models tells clients which models read images, by the rule a run pin uses.
        var json = snapshot.ToJson()["catalog"]!.AsArray();
        Assert.False(json.Single(entry => (string?)entry!["id"] == "MiniMax-M2")!["accepts_images"]!.GetValue<bool>());
        Assert.True(json.Single(entry => (string?)entry!["id"] == "MiniMax-VL")!["accepts_images"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_run_pins_and_sends_an_anthropic_gateway_over_messages()
    {
        var cache = await CacheAsync();
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with { Backend = "models.dev:minimax", Model = "MiniMax-M2" }, default);
        var credentials = new Keyed();
        var catalog = new ProviderCatalogService(new HttpClient(new Refusing()), credentials, TimeProvider.System, cache);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var run = await harness.SeedRunAsync();
        var pins = new SqliteAgentRunProviderSelectionStore(harness.Options);
        await using var factory = new AgentProviderSnapshotFactory(store, credentials, catalog, pins, (_, _) => Task.FromResult(false));

        var bootstrap = await factory.GetAsync(run.Run.RunId, default);

        Assert.Equal(ProviderProtocols.AnthropicMessages, bootstrap.Protocol);
        Assert.Equal(new Uri("https://api.minimax.io/anthropic/v1/messages"), bootstrap.Endpoint);
        var pin = (await pins.GetAsync(run.Run.RunId, default))?.Selection;
        Assert.Equal(ProviderProtocols.AnthropicMessages, pin?.Protocol);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, CandidateKeyVerdict.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest, CandidateKeyVerdict.Accepted)]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, CandidateKeyVerdict.Unchecked)]
    // A route that reads the body before the key proves nothing either way.
    [InlineData(HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, CandidateKeyVerdict.Unchecked)]
    public async Task A_catalog_served_key_is_checked_with_an_empty_generation_request(
        HttpStatusCode anonymous, HttpStatusCode keyed, CandidateKeyVerdict verdict)
    {
        var handler = new GenerationRoute(anonymous, keyed);
        var catalog = new ProviderCatalogService(new HttpClient(handler), new Keyed(), TimeProvider.System, await CacheAsync());

        var result = await catalog.VerifyCandidateCredentialAsync("models.dev:minimax", "synthetic-key",
            Settings("custom", "m"), default);

        Assert.Equal(verdict, result);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new Uri("https://api.minimax.io/anthropic/v1/messages"), request.Uri);
            Assert.Equal("{}", request.Body);
        });
        // The anonymous request carries no key; the keyed one uses Anthropic's header.
        Assert.Null(handler.Requests[0].ApiKey);
        if (handler.Requests.Count > 1) Assert.Equal("synthetic-key", handler.Requests[1].ApiKey);
    }

    [Fact]
    public async Task A_saved_effort_runs_at_default_on_a_model_that_now_publishes_no_levels()
    {
        // A gateway moved from chat completions to Messages loses its effort fields; the
        // level saved under the old route must not stop the next run from starting.
        var catalog = new ProviderCatalogService(new HttpClient(new Refusing()), new Keyed(), TimeProvider.System, await CacheAsync());
        var snapshot = await catalog.GetAsync("models.dev:minimax", Settings("models.dev:minimax", "MiniMax-M2"), true, default);

        Assert.Empty(ProviderReasoningPreferences.ResolveSaved(snapshot, "MiniMax-M2", "high"));
        Assert.Throws<ProviderCatalogException>(() => ProviderReasoningPreferences.Resolve(snapshot, "MiniMax-M2", "high"));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, CandidateKeyVerdict.Rejected)]
    [InlineData(HttpStatusCode.OK, CandidateKeyVerdict.Accepted)]
    public async Task An_xai_key_is_checked_at_its_key_endpoint_where_400_means_wrong(
        HttpStatusCode status, CandidateKeyVerdict verdict)
    {
        var handler = new GenerationRoute(status, status);
        var catalog = new ProviderCatalogService(new HttpClient(handler), new Keyed(), TimeProvider.System, await CacheAsync());

        Assert.Equal(verdict, await catalog.VerifyCandidateCredentialAsync("models.dev:xai", "synthetic-key", Settings("custom", "m"), default));
        Assert.Equal(new Uri("https://api.x.ai/v1/api-key"), Assert.Single(handler.Requests).Uri);
    }

    private async Task<string> CacheAsync()
    {
        Directory.CreateDirectory(_root);
        var filler = string.Join(",", Enumerable.Range(0, 60)
            .Select(i => $"\"prov{i:000}\":{{\"api\":\"https://prov{i:000}.example/v1\"}}"));
        var payload = Catalog.Trim().TrimEnd('}') + "," + filler + "}";
        var cache = Path.Combine(_root, "models-dev.json");
        await File.WriteAllTextAsync(cache, $$"""{"fetched_at":"2099-01-01T00:00:00+00:00","payload":{{payload}}}""");
        return cache;
    }

    private static SettingsSnapshot Settings(string backend, string model) =>
        new(new System.Text.Json.Nodes.JsonObject(), backend, model, "default", "http://localhost:8000/v1", "", "");

    public void Dispose()
    {
        SqliteTestPools.ClearHarnessDatabase(DataPaths.Resolve(_root, _root, _root, _root).AgentHarnessDatabase);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class Keyed : ICredentialStore
    {
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("synthetic-stored-key");
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a Models.dev-served catalog must not fetch " + request.RequestUri);
    }

    private sealed class GenerationRoute(HttpStatusCode anonymous, HttpStatusCode keyed) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string Body, string? ApiKey)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            key ??= request.Headers.Authorization?.Parameter;
            Requests.Add((request.Method, request.RequestUri!, body, key));
            return new HttpResponseMessage(key is null ? anonymous : keyed);
        }
    }
}
