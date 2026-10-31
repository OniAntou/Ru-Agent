using System.Net;
using System.Text.Json.Nodes;
using RuAgent.Harness.Persistence;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using RuBackend.Tests.Harness;
using Xunit;

namespace RuBackend.Tests.Providers;

/// <summary>Named custom providers (ADR 0034).</summary>
public sealed class CustomProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-custom-provider-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1")]
    [InlineData(" http://localhost:1234/v1/ ", "http://localhost:1234/v1")]
    [InlineData("https://gw.example/v1/chat/completions", "https://gw.example/v1")]
    [InlineData("https://gw.example/api/v1/models", "https://gw.example/api/v1")]
    [InlineData("https://relay.example/anthropic/v1/messages", "https://relay.example/anthropic/v1")]
    public void A_pasted_url_is_normalized_to_the_base_both_routes_hang_off(string pasted, string expected)
    {
        Assert.Equal(expected, CustomProviders.NormalizeBaseUrl(pasted));
    }

    [Theory]
    [InlineData("ftp://gw.example/v1")]
    [InlineData("https://user:secret@gw.example/v1")]
    [InlineData("https://gw.example/v1?key=1")]
    [InlineData("gw.example/v1")]
    public void A_url_that_cannot_be_an_endpoint_is_refused(string value)
    {
        Assert.Throws<ArgumentException>(() => CustomProviders.NormalizeBaseUrl(value));
    }

    [Theory]
    [InlineData("https://gw.example/v1", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://ollama.localhost/v1", true)]
    [InlineData("http://127.0.0.5:8000/v1", true)]
    [InlineData("http://[::1]:8000/v1", true)]
    [InlineData("http://192.168.1.5:8000/v1", false)]
    [InlineData("http://gw.example/v1", false)]
    public void A_key_travels_only_over_https_or_to_this_machine(string url, bool allowed)
    {
        Assert.Equal(allowed, CustomProviders.AllowsCredential(new Uri(url)));
    }

    [Fact]
    public void A_settings_save_normalizes_valid_definitions_and_rejects_any_invalid_one_whole()
    {
        var raw = new JsonObject();
        CustomProviders.Apply(new JsonObject { [CustomProviders.SettingField] = Definitions("home", "http://localhost:11434") }, raw);
        var stored = Assert.Single(CustomProviders.Read(Settings(raw)));
        Assert.Equal("http://localhost:11434/v1", stored.BaseUrl);
        Assert.Equal("custom:home", stored.Backend);
        Assert.Equal("custom_provider_home_key", stored.Account);

        foreach (var bad in new JsonNode[]
                 {
                     new JsonArray(Definition("home", "http://localhost:1/v1"), Definition("home", "http://localhost:2/v1")),
                     new JsonArray(Definition("Home", "http://localhost:1/v1")),
                     new JsonArray(Definition("a_b", "http://localhost:1/v1")),
                     new JsonArray(Definition("home", "http://localhost:1/v1", protocol: "openai_responses")),
                     new JsonArray(Definition("home", "http://localhost:1/v1", models: new JsonArray(new JsonObject { ["id"] = "m", ["context_window"] = 100 }))),
                     new JsonObject(),
                 })
        {
            var target = new JsonObject();
            Assert.Throws<ArgumentException>(() => CustomProviders.Apply(new JsonObject { [CustomProviders.SettingField] = bad }, target));
            Assert.False(target.ContainsKey(CustomProviders.SettingField));
        }
    }

    [Fact]
    public void A_user_id_never_takes_a_first_party_identity()
    {
        var raw = new JsonObject
        {
            [CustomProviders.SettingField] = new JsonArray(
                Definition("anthropic", "https://relay.example/v1", protocol: ProviderProtocols.AnthropicMessages),
                Definition("openai", "https://gw.example/v1")),
        };
        var relay = ProviderSelection.From(Settings(raw, "custom:anthropic", "claude-x"));
        var gateway = ProviderSelection.From(Settings(raw, "custom:openai", "gpt-x"));

        Assert.Equal("custom:anthropic", relay.Alias);
        Assert.Equal("custom_provider_anthropic_key", relay.Account);
        Assert.Equal("https://relay.example/v1/messages", relay.CompletionUri.AbsoluteUri);
        Assert.Equal("https://gw.example/v1/chat/completions", gateway.CompletionUri.AbsoluteUri);
        Assert.False(relay.RequiresCredential);
        Assert.False(ProviderWebSearchPolicy.Supports(relay, new JsonObject()));
        Assert.False(ProviderWebSearchPolicy.Supports(gateway, new JsonObject()));
        Assert.Throws<ArgumentException>(() => ProviderSelection.From(Settings(raw, "custom:missing", "m")));
    }

    [Fact]
    public async Task The_catalog_is_the_configured_list_and_never_reaches_the_network()
    {
        var raw = new JsonObject
        {
            [CustomProviders.SettingField] = new JsonArray(Definition("home", "http://localhost:11434/v1",
                models: new JsonArray(new JsonObject { ["id"] = "qwen3:8b", ["context_window"] = 16384 }, new JsonObject { ["id"] = "llama3" }))),
        };
        var catalog = new ProviderCatalogService(new HttpClient(new ExplodingHandler()), new MemoryCredentials(), TimeProvider.System);

        var snapshot = await catalog.GetAsync("custom:home", Settings(raw, "custom:home", "qwen3:8b"), refresh: true, default);

        Assert.Equal("ok", snapshot.Status);
        Assert.Equal("custom:home", snapshot.Backend);
        Assert.Equal(new[] { "qwen3:8b", "llama3" }, snapshot.Models);
        Assert.Equal(16384, snapshot.Catalog[0].ContextLength);
        Assert.Null(snapshot.Catalog[1].ContextLength);
        Assert.All(snapshot.Catalog, model => Assert.Equal(CatalogReasoningStates.Unknown, model.Reasoning.State));
    }

    [Fact]
    public async Task Discovery_skips_unusable_entries_and_reads_the_served_context_length()
    {
        var raw = new JsonObject { [CustomProviders.SettingField] = new JsonArray(Definition("vllm", "http://localhost:8000/v1")) };
        var handler = new RecordingHandler("""
            {"data":[{"id":"good","max_model_len":32768},{"id":""},{"name":"no-id"},{"id":"other","context_length":8192}]}
            """);
        var catalog = new ProviderCatalogService(new HttpClient(handler),
            new MemoryCredentials(("custom_provider_vllm_key", "local-secret")), TimeProvider.System);

        var discovered = await catalog.DiscoverCustomModelsAsync("custom:vllm", Settings(raw), default);

        Assert.Equal("ok", discovered.Status);
        Assert.Equal(new[] { "good", "other" }, discovered.Models);
        Assert.Equal(32768, discovered.Catalog[0].ContextLength);
        Assert.Equal(8192, discovered.Catalog[1].ContextLength);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:8000/v1/models", request.Uri);
        Assert.Equal("Bearer local-secret", request.Authorization);
        Assert.Empty(CustomProviders.Read(Settings(raw)).Single().Models);
    }

    [Fact]
    public async Task Discovery_for_an_anthropic_relay_sends_the_anthropic_headers()
    {
        var raw = new JsonObject
        {
            [CustomProviders.SettingField] = new JsonArray(
                Definition("relay", "https://relay.example/v1", protocol: ProviderProtocols.AnthropicMessages)),
        };
        var handler = new RecordingHandler("""{"data":[{"id":"claude-x"}]}""");
        var catalog = new ProviderCatalogService(new HttpClient(handler),
            new MemoryCredentials(("custom_provider_relay_key", "relay-secret")), TimeProvider.System);

        var discovered = await catalog.DiscoverCustomModelsAsync("custom:relay", Settings(raw), default);

        Assert.Equal("ok", discovered.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("relay-secret", request.ApiKey);
        Assert.Equal("2023-06-01", request.AnthropicVersion);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public async Task A_key_is_never_sent_over_plain_http_to_another_computer()
    {
        var raw = new JsonObject { [CustomProviders.SettingField] = new JsonArray(Definition("lan", "http://192.168.1.5:8000/v1")) };
        var handler = new RecordingHandler("""{"data":[]}""");
        var keyed = new ProviderCatalogService(new HttpClient(handler),
            new MemoryCredentials(("custom_provider_lan_key", "lan-secret")), TimeProvider.System);
        var keyless = new ProviderCatalogService(new HttpClient(handler), new MemoryCredentials(), TimeProvider.System);

        var refused = await keyed.DiscoverCustomModelsAsync("custom:lan", Settings(raw), default);
        Assert.Equal("error", refused.Status);
        Assert.Equal(CustomProviders.InsecureEndpointCode, refused.ErrorCode);
        Assert.Empty(handler.Requests);

        var allowed = await keyless.DiscoverCustomModelsAsync("custom:lan", Settings(raw), default);
        Assert.Equal("ok", allowed.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_run_pins_the_users_context_window_and_fails_closed_when_the_provider_changes()
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        var providers = new JsonArray(Definition("home", "http://localhost:11434/v1",
            models: new JsonArray(new JsonObject { ["id"] = "qwen3:8b", ["context_window"] = 16384 })));
        var initial = await store.ReadAsync(default);
        var raw = initial.Raw.DeepClone().AsObject();
        raw[CustomProviders.SettingField] = providers;
        await store.SaveAsync(initial with { Raw = raw, Backend = "custom:home", Model = "qwen3:8b" }, default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var credentials = new MemoryCredentials(("custom_provider_home_key", "home-secret"));
        var catalog = new ProviderCatalogService(new HttpClient(new ExplodingHandler()), credentials, TimeProvider.System);
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);

        var pinned = await harness.SeedRunAsync();
        await using (var factory = new AgentProviderSnapshotFactory(store, credentials, catalog, selections, (_, _) => Task.FromResult(false)))
        {
            var snapshot = await factory.GetAsync(pinned.Run.RunId, default);
            Assert.Equal("http://localhost:11434/v1/chat/completions", snapshot.Endpoint.AbsoluteUri);
            Assert.Equal("home-secret", snapshot.Credential);
            Assert.Equal("qwen3:8b", snapshot.Model);
        }

        var selection = (await selections.GetAsync(pinned.Run.RunId, default))!.Selection!;
        Assert.Equal("custom:home", selection.Backend);
        Assert.Equal("custom:home", selection.Alias);
        Assert.Equal("custom_provider_home_key", selection.Account);
        Assert.Equal(16384, selection.ContextWindowTokens);
        Assert.Equal("user_setting", selection.ContextWindowSource);

        // A model removed from the list: the pinned run fails closed instead of switching.
        var current = await store.ReadAsync(default);
        var withoutModel = current.Raw.DeepClone().AsObject();
        withoutModel[CustomProviders.SettingField] = new JsonArray(Definition("home", "http://localhost:11434/v1"));
        await store.SaveAsync(current with { Raw = withoutModel }, default);
        await using (var factory = new AgentProviderSnapshotFactory(store, credentials, catalog, selections, (_, _) => Task.FromResult(false)))
        {
            var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(pinned.Run.RunId, default));
            Assert.Equal("model_unavailable", error.Code);
        }

        // The provider deleted: the pinned run fails closed too.
        current = await store.ReadAsync(default);
        var withoutProvider = current.Raw.DeepClone().AsObject();
        withoutProvider[CustomProviders.SettingField] = new JsonArray();
        await store.SaveAsync(current with { Raw = withoutProvider }, default);
        await using (var factory = new AgentProviderSnapshotFactory(store, credentials, catalog, selections, (_, _) => Task.FromResult(false)))
        {
            var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(pinned.Run.RunId, default));
            Assert.Equal("provider_selection_invalid", error.Code);
        }
    }

    [Fact]
    public async Task A_run_refuses_to_send_a_key_over_plain_http_to_another_computer()
    {
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        var initial = await store.ReadAsync(default);
        var raw = initial.Raw.DeepClone().AsObject();
        raw[CustomProviders.SettingField] = new JsonArray(Definition("lan", "http://192.168.1.5:8000/v1",
            models: new JsonArray(new JsonObject { ["id"] = "m" })));
        await store.SaveAsync(initial with { Raw = raw, Backend = "custom:lan", Model = "m" }, default);
        await using var harness = await BackendHarnessTestFixture.CreateAsync();
        var credentials = new MemoryCredentials(("custom_provider_lan_key", "lan-secret"));
        var catalog = new ProviderCatalogService(new HttpClient(new ExplodingHandler()), credentials, TimeProvider.System);
        var selections = new SqliteAgentRunProviderSelectionStore(harness.Options);
        await using var factory = new AgentProviderSnapshotFactory(store, credentials, catalog, selections, (_, _) => Task.FromResult(false));
        var run = await harness.SeedRunAsync();

        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => factory.GetAsync(run.Run.RunId, default));

        Assert.Equal(CustomProviders.InsecureEndpointCode, error.Code);
    }

    [Fact]
    public async Task Settings_store_a_providers_key_under_its_own_account_and_refuse_insecure_or_unknown_ones()
    {
        var settings = new MemorySettingsStore(new SettingsSnapshot(new JsonObject(), "OpenAI", "", "default",
            "http://localhost:8000/v1", "gpt-x", ""));
        var credentials = new MemoryCredentials();
        var service = new SettingsTransitionService(settings, credentials);

        var saved = await service.ApplyAsync(new JsonObject
        {
            [CustomProviders.SettingField] = new JsonArray(Definition("home", "http://localhost:11434",
                models: new JsonArray(new JsonObject { ["id"] = "qwen3:8b" }))),
            ["custom_provider_id"] = "home",
            ["custom_provider_api_key"] = "home-secret",
            ["backend_choice"] = "custom:home",
            ["model"] = "qwen3:8b",
        }, default);
        Assert.True(saved.Success, saved.Message);
        Assert.Equal("home-secret", credentials.Value("custom_provider_home_key"));
        Assert.Equal("custom:home", settings.Saved!.Backend);
        Assert.Equal("http://localhost:11434/v1", CustomProviders.Read(settings.Saved).Single().BaseUrl);

        // Moving a keyed provider to plain HTTP on another computer is refused, key untouched.
        var moved = await service.ApplyAsync(new JsonObject
        {
            [CustomProviders.SettingField] = new JsonArray(Definition("home", "http://192.168.1.5:11434/v1")),
        }, default);
        Assert.False(moved.Success);
        Assert.Equal(CustomProviders.InsecureEndpointCode, moved.Code);
        Assert.Equal("http://localhost:11434/v1", CustomProviders.Read(settings.Saved).Single().BaseUrl);

        var undefined = await service.ApplyAsync(new JsonObject
        {
            ["custom_provider_id"] = "ghost",
            ["custom_provider_api_key"] = "ghost-secret",
        }, default);
        Assert.False(undefined.Success);
        Assert.Null(credentials.Value("custom_provider_ghost_key"));

        var unknownBackend = await service.ApplyAsync(new JsonObject { ["backend_choice"] = "custom:ghost" }, default);
        Assert.False(unknownBackend.Success);

        var removed = await service.RemoveProviderAsync("custom:home", default);
        Assert.True(removed.Success);
        Assert.Null(credentials.Value("custom_provider_home_key"));
    }

    [Fact]
    public async Task The_legacy_slot_refuses_a_key_for_plain_http_to_another_computer()
    {
        var settings = new MemorySettingsStore(new SettingsSnapshot(new JsonObject(), "Custom OpenAI", "", "default",
            "http://localhost:8000/v1", "", ""));
        var credentials = new MemoryCredentials();
        var service = new SettingsTransitionService(settings, credentials);

        var refused = await service.ApplyAsync(new JsonObject
        {
            ["custom_url"] = "http://10.0.0.7:8000/v1",
            ["custom_api_key"] = "legacy-secret",
        }, default);

        Assert.False(refused.Success);
        Assert.Equal(CustomProviders.InsecureEndpointCode, refused.Code);
        Assert.Null(credentials.Value("custom_api_key"));

        var normalized = await service.ApplyAsync(new JsonObject { ["custom_url"] = "http://localhost:1234/v1/chat/completions" }, default);
        Assert.True(normalized.Success, normalized.Message);
        Assert.Equal("http://localhost:1234/v1", settings.Saved!.CustomUrl);
    }

    public void Dispose()
    {
        SqliteTestPools.ClearHarnessDatabase(DataPaths.Resolve(_root, _root, _root, _root).AgentHarnessDatabase);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static SettingsSnapshot Settings(JsonObject raw, string backend = "OpenAI", string model = "") =>
        new(raw, backend, model, "default", "http://localhost:8000/v1", "", "");

    private static JsonArray Definitions(string id, string url) => new(Definition(id, url));

    private static JsonObject Definition(string id, string url, string protocol = ProviderProtocols.ChatCompletions, JsonArray? models = null) => new()
    {
        ["id"] = id,
        ["display_name"] = "Provider " + id,
        ["base_url"] = url,
        ["protocol"] = protocol,
        ["models"] = models ?? new JsonArray(),
    };

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a custom provider's configured catalog must not fetch anything");
    }

    private sealed record SeenRequest(string Uri, string? Authorization, string? ApiKey, string? AnthropicVersion);

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SeenRequest(
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null,
                request.Headers.TryGetValues("anthropic-version", out var versions) ? versions.Single() : null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class MemorySettingsStore(SettingsSnapshot snapshot) : ISettingsStore
    {
        public SettingsSnapshot? Saved { get; private set; }

        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Saved ?? snapshot);

        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken)
        {
            Saved = candidate;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryCredentials(params (string Account, string Secret)[] entries) : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = entries.ToDictionary(item => item.Account, item => item.Secret, StringComparer.Ordinal);

        public string? Value(string account) => _values.GetValueOrDefault(account);

        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            Task.FromResult(_values.GetValueOrDefault(accountName));

        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken)
        {
            _values[accountName] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        {
            _values.Remove(accountName);
            return Task.CompletedTask;
        }
    }
}
