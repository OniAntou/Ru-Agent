using System.Net;
using System.Text.Json.Nodes;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class ProviderCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly Credentials _credentials = new();
    private readonly Handler _handler = new();

    private async Task<SettingsSnapshot> SettingsAsync() =>
        await new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root)).ReadAsync(default);

    private ProviderCatalogService Service() => new(new HttpClient(_handler), _credentials, _clock);

    private ProviderCatalogService ModelsDevService() =>
        new(new HttpClient(_handler), _credentials, _clock, Path.Combine(_root, "models-dev.json"));

    private const string TwoProviderJson =
        "{\"openrouter\":{\"api\":\"https://openrouter.ai/api/v1\"},\"second\":{\"api\":\"https://example.com/v1\"}}";

    private static string ManyProviderJson(int count)
    {
        var entries = Enumerable.Range(0, count)
            .Select(i => $"\"prov{i:000}\":{{\"api\":\"https://prov{i:000}.example/v1\"}}");
        return "{" + string.Join(",", entries) + "}";
    }

    /// <summary>
    /// The same payload as <see cref="ManyProviderJson"/> plus one named provider, so a
    /// test can fetch a catalog for a specific Models.dev backend.
    /// </summary>
    private static string ManyProviderJsonWith(int count, string id, string api)
    {
        var entries = Enumerable.Range(0, count)
            .Select(i => $"\"prov{i:000}\":{{\"api\":\"https://prov{i:000}.example/v1\"}}")
            .Prepend($"\"{id}\":{{\"api\":\"{api}\"}}");
        return "{" + string.Join(",", entries) + "}";
    }

    /// <summary>Writes a Models.dev cache the service will trust on read.</summary>
    private async Task<string> WriteModelsDevCacheAsync(string providersJson)
    {
        var cacheFile = Path.Combine(_root, "models-dev.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(cacheFile,
            $"{{\"fetched_at\":\"2099-01-01T00:00:00+00:00\",\"payload\":{providersJson}}}");
        return cacheFile;
    }

    [Fact]
    public async Task ModelsDev_catalog_rejects_plain_http_api_urls()
    {
        _handler.Json = "{\"plain\":{\"api\":\"http://plain.example/v1\"},\"secure\":{\"api\":\"https://secure.example/v1\"}}";
        var service = ModelsDevService();
        var providers = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal("secure", Assert.Single(providers).Id);
    }

    [Fact]
    public async Task Generic_models_dev_backend_rejects_plain_http_base_url()
    {
        var settings = (await SettingsAsync()) with { Backend = "models.dev:plain", Model = "x" };
        var catalog = new[] { new ModelsDevProvider("plain", "Plain", "http://plain.example/v1", []) };
        Assert.Throws<ArgumentException>(() => ProviderSelection.From(settings, catalog));
    }

    [Fact]
    public async Task ModelsDev_small_payload_is_served_but_never_cached()
    {
        _handler.Json = "{\"openrouter\":{\"api\":\"https://openrouter.ai/api/v1\"}}";
        var service = ModelsDevService();
        var providers = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal("openrouter", Assert.Single(providers).Id);
        Assert.False(File.Exists(Path.Combine(_root, "models-dev.json")));
    }

    [Fact]
    public async Task ModelsDev_small_payload_keeps_good_stale_cache()
    {
        _handler.Json = ManyProviderJson(60);
        var service = ModelsDevService();
        var first = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal(60, first.Count);
        _clock.Advance(TimeSpan.FromHours(25));
        _handler.Json = "{\"openrouter\":{\"api\":\"https://openrouter.ai/api/v1\"}}";
        var offline = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal(60, offline.Count);
        var cacheText = await File.ReadAllTextAsync(Path.Combine(_root, "models-dev.json"));
        Assert.Contains("prov059", cacheText);
    }

    [Fact]
    public async Task ModelsDev_catalog_lists_generic_provider()
    {
        _handler.Json = ManyProviderJson(60).Replace("\"prov000\"", "\"openrouter\"").Replace("https://prov000.example/v1", "https://openrouter.ai/api/v1");
        var service = ModelsDevService();
        var providers = await service.GetModelsDevAsync(CancellationToken.None);
        var openrouter = Assert.Single(providers, p => p.Id == "openrouter");
        Assert.Equal("https://openrouter.ai/api/v1", openrouter.BaseUrl);
    }

    [Fact]
    public async Task ModelsDev_base_url_keeps_the_api_path_prefix()
    {
        // Catalog and chat URLs are appended to BaseUrl: authority-only
        // 404s every provider whose API lives under a path (…/api/v1,
        // …/zen/v1), e.g. Kilo and OpenCode Zen.
        var payload = ManyProviderJson(60);
        payload = payload.Replace("\"prov000\":{\"api\":\"https://prov000.example/v1\"}", "\"kilo\":{\"name\":\"Kilo\",\"api\":\"https://api.kilo.ai/api/gateway/\"}");
        _handler.Json = payload;
        var service = ModelsDevService();
        var providers = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal("https://api.kilo.ai/api/gateway", Assert.Single(providers, p => p.Id == "kilo").BaseUrl);
    }

    [Fact]
    public async Task ModelsDev_parse_keeps_model_ids()
    {
        var payload = ManyProviderJson(60);
        payload = payload.Replace("\"prov000\":{\"api\":\"https://prov000.example/v1\"}", "\"cf\":{\"api\":\"https://x.example/v1\",\"models\":{\"@cf/a\":{},\"@cf/b\":{}}}");
        _handler.Json = payload;
        var service = ModelsDevService();
        var providers = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal(["@cf/a", "@cf/b"], Assert.Single(providers, p => p.Id == "cf").Models);
    }

    [Fact]
    public async Task Cloudflare_override_fills_base_url_from_account_id()
    {
        var settings = (await SettingsAsync()) with
        {
            Backend = "models.dev:cloudflare-workers-ai",
            CloudflareAccountId = "abc123",
        };
        var catalog = new[] { new ModelsDevProvider("cloudflare-workers-ai", "Cloudflare", "https://api.cloudflare.com/client/v4/accounts/${CLOUDFLARE_ACCOUNT_ID}/ai/v1", []) };
        var selection = ProviderSelection.From(settings, catalog);
        Assert.Equal(new Uri("https://api.cloudflare.com/client/v4/accounts/abc123/ai/v1"), selection.BaseUri);
        Assert.Equal(new Uri("https://api.cloudflare.com/client/v4/accounts/abc123/ai/v1/models"), selection.ModelsUri);
        Assert.Equal(new Uri("https://api.cloudflare.com/client/v4/accounts/abc123/ai/v1/chat/completions"), selection.CompletionUri);
        Assert.Equal("cloudflare-workers-ai_api_key", selection.Account);
    }

    [Fact]
    public async Task Cloudflare_override_without_account_id_fails_closed()
    {
        var settings = (await SettingsAsync()) with { Backend = "models.dev:cloudflare-workers-ai" };
        var catalog = new[] { new ModelsDevProvider("cloudflare-workers-ai", "Cloudflare", "https://api.cloudflare.com/client/v4/accounts/${CLOUDFLARE_ACCOUNT_ID}/ai/v1", []) };
        Assert.Throws<ArgumentException>(() => ProviderSelection.From(settings, catalog));
    }

    [Fact]
    public async Task Cloudflare_catalog_comes_from_models_dev_without_http()
    {
        var payload = ManyProviderJson(60);
        payload = payload.Replace("\"prov000\":{\"api\":\"https://prov000.example/v1\"}", "\"cloudflare-workers-ai\":{\"name\":\"Cloudflare\",\"api\":\"https://api.cloudflare.com/client/v4/accounts/${CLOUDFLARE_ACCOUNT_ID}/ai/v1\",\"models\":{\"@cf/a\":{},\"@cf/b\":{}}}");
        payload = payload.Replace("\"prov001\":{\"api\":\"https://prov001.example/v1\"}", "\"other\":{\"api\":\"https://other.example/v1\"}");
        _handler.Json = payload;
        var service = ModelsDevService();
        var settings = (await SettingsAsync()) with
        {
            Backend = "models.dev:cloudflare-workers-ai",
            CloudflareAccountId = "abc123",
            Model = "@cf/a",
        };
        var snapshot = await service.GetAsync(settings, refresh: true, CancellationToken.None);
        Assert.Equal("ok", snapshot.Status);
        Assert.Equal(["@cf/a", "@cf/b"], snapshot.Models);
        Assert.Equal(1, _handler.Count);
    }

    [Fact]
    public async Task ModelsDev_provider_without_name_falls_back_to_id()
    {
        // "zeta" carries no name field: the parser must fall back to the id.
        _handler.Json = ManyProviderJson(60)
            .Replace("\"prov000\":{\"api\":\"https://prov000.example/v1\"}", "\"zeta\":{\"api\":\"https://zeta.example/v1\"}")
            .Replace("\"prov001\":{\"api\":\"https://prov001.example/v1\"}", "\"alpha\":{\"api\":\"https://alpha.example/v1\"}");
        var service = ModelsDevService();
        var providers = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal("zeta", Assert.Single(providers, p => p.Id == "zeta").Name);
    }

    [Fact]
    public async Task ModelsDev_suspicious_payload_is_served_but_never_cached()
    {
        _handler.Json = "{\"openrouter\":{\"api\":\"https://openrouter.ai/api/v1\"}}";
        var service = ModelsDevService();
        var providers = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal("openrouter", Assert.Single(providers).Id);
        Assert.False(File.Exists(Path.Combine(_root, "models-dev.json")));
    }

    [Fact]
    public async Task ModelsDev_suspicious_payload_keeps_good_stale_cache()
    {
        _handler.Json = ManyProviderJson(60);
        var service = ModelsDevService();
        var first = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal(60, first.Count);
        _clock.Advance(TimeSpan.FromHours(25));
        _handler.Json = "{\"openrouter\":{\"api\":\"https://openrouter.ai/api/v1\"}}";
        var offline = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal(60, offline.Count);
        var cacheText = await File.ReadAllTextAsync(Path.Combine(_root, "models-dev.json"));
        Assert.Contains("prov059", cacheText);
    }

    [Fact]
    public async Task ModelsDev_empty_payload_keeps_good_stale_cache()
    {
        _handler.Json = ManyProviderJson(60);
        var service = ModelsDevService();
        Assert.Equal(60, (await service.GetModelsDevAsync(CancellationToken.None)).Count);
        _clock.Advance(TimeSpan.FromHours(25));
        _handler.Json = "{}";
        Assert.Equal(60, (await service.GetModelsDevAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ModelsDev_suspicious_cached_list_triggers_refetch()
    {
        var cacheFile = Path.Combine(_root, "models-dev.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(cacheFile,
            """{"fetched_at":"2099-01-01T00:00:00+00:00","payload":{"openrouter":{"api":"https://openrouter.ai/api/v1"}}}""");
        _handler.Json = ManyProviderJson(60);
        var service = new ProviderCatalogService(new HttpClient(_handler), _credentials, _clock, cacheFile);
        var providers = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal(60, providers.Count);
        Assert.Equal(1, _handler.Count);
    }

    /// <summary>
    /// A catalog response names the provider in the canonical form the caller asked for.
    /// </summary>
    /// <remarks>
    /// It answered with the bare Models.dev id while callers group by "models.dev:&lt;id&gt;",
    /// so every response looked like it belonged to a different provider and the desktop
    /// picker threw it away: the fetch succeeded with 458 models and the list still read
    /// "No available models". Only the custom endpoint survived, because there the id and
    /// the requested value are the same string.
    /// </remarks>
    [Fact]
    public async Task Generic_backend_snapshot_reports_the_backend_the_caller_asked_for()
    {
        // A cache is trusted on read only above MinimumModelsDevProviders, so a
        // one-provider fixture would be deleted as corrupt before it is ever used.
        var cacheFile = await WriteModelsDevCacheAsync(
            ManyProviderJsonWith(60, "openrouter", "https://openrouter.ai/api/v1"));
        _handler.Json = "{\"data\":[{\"id\":\"model-a\"}]}";
        var service = new ProviderCatalogService(new HttpClient(_handler), _credentials, _clock, cacheFile);
        var settings = (await SettingsAsync()) with { Backend = "models.dev:openrouter", Model = "model-a" };

        var snapshot = await service.GetAsync(settings, false, CancellationToken.None);

        Assert.Equal("ok", snapshot.Status);
        Assert.Equal("models.dev:openrouter", snapshot.Backend);
    }

    [Fact]
    public async Task ModelsDev_catalog_uses_cache_and_falls_back_offline()
    {
        _handler.Json = ManyProviderJson(60);
        var service = ModelsDevService();
        var first = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal(60, first.Count);
        Assert.Equal(1, _handler.Count);
        var cached = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal(60, cached.Count);
        Assert.Equal(1, _handler.Count);
        _clock.Advance(TimeSpan.FromHours(25));
        _handler.Status = HttpStatusCode.ServiceUnavailable;
        var offline = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal(60, offline.Count);
    }

    [Fact]
    public async Task ModelsDev_catalog_without_cache_fails_closed_to_empty()
    {
        _handler.Status = HttpStatusCode.ServiceUnavailable;
        var service = ModelsDevService();
        Assert.Empty(await service.GetModelsDevAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ModelsDev_concurrent_calls_share_one_fetch()
    {
        _handler.Json = ManyProviderJson(60);
        var service = ModelsDevService();
        _handler.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 8).Select(_ => service.GetModelsDevAsync(CancellationToken.None)).ToArray();
        await _handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _handler.Hold.SetResult();
        await Task.WhenAll(calls);
        Assert.Equal(1, _handler.Count);
        Assert.All(calls, call => Assert.Equal(60, call.Result.Count));
    }

    [Fact]
    public async Task ModelsDev_corrupt_cache_falls_back_to_fetch()
    {
        var cacheFile = Path.Combine(_root, "models-dev.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(cacheFile, "not-json{{{");
        _handler.Json = "{\"openrouter\":{\"api\":\"https://openrouter.ai/api/v1\"}}";
        var service = new ProviderCatalogService(new HttpClient(_handler), _credentials, _clock, cacheFile);
        var providers = await service.GetModelsDevAsync(CancellationToken.None);
        Assert.Equal("openrouter", Assert.Single(providers).Id);
        Assert.Equal(1, _handler.Count);
    }

    [Fact]
    public async Task Generic_models_dev_backend_resolves_base_uri_from_catalog()
    {
        var settings = (await SettingsAsync()) with { Backend = "models.dev:openrouter", Model = "x" };
        var catalog = new[] { new ModelsDevProvider("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", []) };
        var selection = ProviderSelection.From(settings, catalog);
        Assert.Equal("chat_completions", selection.Protocol);
        Assert.Equal(new Uri("https://openrouter.ai/api/v1"), selection.BaseUri);
    }

    [Fact]
    public async Task Unknown_models_dev_backend_fails_closed()
    {
        var settings = (await SettingsAsync()) with { Backend = "models.dev:nope" };
        Assert.Throws<ArgumentException>(() => ProviderSelection.From(settings, []));
    }

    [Fact]
    public async Task Generic_models_dev_backend_uses_its_own_credential_account()
    {
        var settings = (await SettingsAsync()) with { Backend = "models.dev:openrouter" };
        var catalog = new[] { new ModelsDevProvider("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", []) };
        var selection = ProviderSelection.From(settings, catalog);
        Assert.Equal("openrouter_api_key", selection.Account);
        Assert.True(selection.RequiresCredential);
        Assert.True(selection.IsModelsDev);
    }

    [Fact]
    public async Task Generic_backend_resolves_catalog_end_to_end()
    {
        _handler.Json = ManyProviderJson(60).Replace("\"prov000\"", "\"openrouter\"").Replace("https://prov000.example/v1", "https://openrouter.ai/api/v1");
        var service = ModelsDevService();
        await service.GetModelsDevAsync(CancellationToken.None);
        _handler.Json = "{\"data\":[{\"id\":\"m1\"}]}";
        var settings = (await SettingsAsync()) with { Backend = "models.dev:openrouter" };
        var snapshot = await service.GetAsync(settings, false, default);
        Assert.Equal("ok", snapshot.Status);
        Assert.Equal(["m1"], snapshot.Models);
        Assert.Contains("openrouter_api_key", _credentials.Accounts);
    }

    [Fact]
    public async Task Generic_alias_resolves_catalog_end_to_end()
    {
        _handler.Json = ManyProviderJson(60).Replace("\"prov000\"", "\"openrouter\"").Replace("https://prov000.example/v1", "https://openrouter.ai/api/v1");
        var service = ModelsDevService();
        await service.GetModelsDevAsync(CancellationToken.None);
        _handler.Json = "{\"data\":[{\"id\":\"m1\"}]}";
        var settings = await SettingsAsync();
        var snapshot = await service.GetAsync("Models.Dev:OpenRouter", settings, false, default);
        Assert.Equal("ok", snapshot.Status);
        Assert.Equal(["m1"], snapshot.Models);
    }

    [Fact]
    public async Task Generic_backend_without_key_fails_closed()
    {
        _handler.Json = ManyProviderJson(60).Replace("\"prov000\"", "\"openrouter\"").Replace("https://prov000.example/v1", "https://openrouter.ai/api/v1");
        var service = ModelsDevService();
        await service.GetModelsDevAsync(CancellationToken.None);
        _credentials.Key = "";
        var settings = (await SettingsAsync()) with { Backend = "models.dev:openrouter" };
        var snapshot = await service.GetAsync(settings, false, default);
        Assert.Equal("error", snapshot.Status);
        Assert.Empty(snapshot.Models);
    }

    [Fact]
    public async Task Cache_refreshes_on_expiry_and_throttles_manual_requests()
    {
        var service = Service();
        var settings = (await SettingsAsync()) with { Backend = "Custom OpenAI" };
        Assert.Equal(new[] { "model-a" }, (await service.GetAsync(settings, false, default)).Models);
        _handler.Json = "{\"data\":[{\"id\":\"model-b\"}]}";
        await service.GetAsync(settings, true, default);
        Assert.Equal(1, _handler.Count);
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(new[] { "model-b" }, (await service.GetAsync(settings, true, default)).Models);
        _clock.Advance(TimeSpan.FromMinutes(6));
        await service.GetAsync(settings, false, default);
        Assert.Equal(3, _handler.Count);
    }

    [Fact]
    public async Task Outage_retains_same_identity_but_valid_empty_replaces_list()
    {
        var service = Service();
        var settings = await SettingsAsync();
        var first = await service.GetAsync(settings, false, default);
        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Status = HttpStatusCode.ServiceUnavailable;
        var failed = await service.GetAsync(settings, false, default);
        Assert.Equal("error", failed.Status);
        Assert.True(failed.Stale);
        Assert.Equal(first.Identity, failed.Identity);
        Assert.Equal(first.Models, failed.Models);
        _clock.Advance(TimeSpan.FromSeconds(31));
        _handler.Status = HttpStatusCode.OK;
        _handler.Json = "{\"data\":[]}";
        var empty = await service.GetAsync(settings, false, default);
        Assert.Equal("ok", empty.Status);
        Assert.Empty(empty.Models);
        Assert.False(empty.Stale);
    }

    [Fact]
    public async Task Key_change_drops_old_catalog_and_only_reads_selected_account()
    {
        var service = Service();
        var settings = (await SettingsAsync()) with { Backend = "Custom OpenAI", CustomUrl = "https://custom.invalid/v1" };
        var first = await service.GetAsync(settings, false, default);
        Assert.Equal("Bearer synthetic-key-1", _handler.Authorization);
        _credentials.Key = "synthetic-key-2";
        _handler.Status = HttpStatusCode.Forbidden;
        var second = await service.GetAsync(settings, false, default);
        Assert.NotEqual(first.Identity, second.Identity);
        Assert.Empty(second.Models);
        Assert.All(_credentials.Accounts, account => Assert.Equal("custom_api_key", account));
        Assert.DoesNotContain("synthetic-key", second.ToJson().ToJsonString());
    }

    [Fact]
    public async Task Concurrent_refreshes_are_single_flight_and_rate_limit_is_honored()
    {
        var service = Service();
        var settings = await SettingsAsync();
        _handler.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 8).Select(_ => service.GetAsync(settings, true, default)).ToArray();
        await _handler.Started.Task;
        Assert.Equal(1, _handler.Count);
        _handler.Hold.SetResult();
        await Task.WhenAll(requests);
        Assert.Equal(1, _handler.Count);
        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Status = HttpStatusCode.TooManyRequests;
        await service.GetAsync(settings, true, default);
        _clock.Advance(TimeSpan.FromSeconds(60));
        await service.GetAsync(settings, true, default);
        Assert.Equal(2, _handler.Count);
    }

    [Fact]
    public async Task Refresh_for_one_scope_does_not_block_a_different_scope()
    {
        var service = Service();
        var firstSettings = await SettingsAsync();
        var secondSettings = firstSettings with
        {
            Backend = "Custom OpenAI",
            CustomUrl = "https://custom.invalid/v1",
        };
        _handler.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = service.GetAsync(firstSettings, true, default);
        await _handler.Started.Task;
        var second = service.GetAsync(secondSettings, true, default);

        await _handler.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _handler.Count);
        _handler.Hold.SetResult();
        await Task.WhenAll(first, second);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[{\"id\":12}]}")]
    public void Malformed_catalog_is_not_an_empty_success(string json) =>
        Assert.Equal("catalog_malformed", Assert.Throws<ProviderCatalogException>(() => ProviderCatalogParser.Parse("Custom OpenAI", json)).Code);

    [Fact]
    public async Task Cancelled_refresh_does_not_poison_the_next_request_throttle()
    {
        var service = Service();
        var settings = await SettingsAsync();
        using var cancelled = new CancellationTokenSource();
        _handler.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = service.GetAsync(settings, true, cancelled.Token);
        await _handler.Started.Task;
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        _handler.Hold = null;
        var next = await service.GetAsync(settings, true, default);
        Assert.Equal("ok", next.Status);
        Assert.Equal(2, _handler.Count);
    }

    [Fact]
    public void Bare_ids_remain_unknown_but_runnable()
    {
        var models = ProviderCatalogParser.Parse("Custom OpenAI", """
          {"data":[{"id":"deepseek-v4-flash-free"},{"id":"gpt-5.4"},{"id":"claude-opus-4.6"}]}
          """);
        Assert.Equal("unknown", models[0].Reasoning.State);
        Assert.Empty(models[0].Reasoning.Options);
        Assert.True(models[1].Supported);
        Assert.True(models[2].Supported);
    }

    [Fact]
    public async Task Preferences_are_per_model_and_default_omits_options()
    {
        var settings = (await SettingsAsync()) with { OpenAiModel = "a" };
        var saved = ProviderReasoningPreferences.Save(settings, "high", ProviderSelection.From(settings));
        Assert.Equal("high", ProviderReasoningPreferences.Read(saved));
        Assert.Equal("default", ProviderReasoningPreferences.Read(saved with { OpenAiModel = "b" }));
        var entry = new CatalogModel("a", true, "chat_completions", new("unknown", []));
        var snapshot = new ProviderCatalogSnapshot("ok", "openai", "id", "a", "default", ["a"], [entry], false, null, _clock.GetUtcNow());
        Assert.Empty(ProviderReasoningPreferences.Resolve(snapshot, "a", "default"));
        Assert.Throws<ProviderCatalogException>(() => ProviderReasoningPreferences.Resolve(snapshot, "a", "high"));
    }

    [Fact]
    public async Task Empty_selected_model_is_not_fabricated_from_a_provider_default()
    {
        var settings = (await SettingsAsync()) with
        {
            Model = string.Empty,
        };

        var selection = ProviderSelection.From(settings);

        Assert.Equal(string.Empty, selection.Model);
    }

    [Theory]
    [InlineData("Custom OpenAI", "custom_api_key")]
    [InlineData("OpenAI", "openai_api_key")]
    [InlineData("Anthropic", "anthropic_api_key")]
    public async Task Provider_selection_uses_only_its_canonical_credential_account(
        string backend, string? expectedAccount)
    {
        var settings = (await SettingsAsync()) with { Backend = backend };

        Assert.Equal(expectedAccount, ProviderSelection.From(settings).Account);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"\"")]
    [InlineData("\"high\\u0000\"")]
    public async Task Malformed_saved_reasoning_is_invalid_instead_of_becoming_default(string malformed)
    {
        var settings = (await SettingsAsync()) with { OpenAiModel = "a" };
        var key = ProviderSelection.From(settings).PreferenceKey;
        settings.Raw["reasoning_preferences"] = new JsonObject { [key] = JsonNode.Parse(malformed) };

        var choice = ProviderReasoningPreferences.Read(settings);

        Assert.Equal(ProviderReasoningPreferences.InvalidChoice, choice);
        var entry = new CatalogModel("a", true, "chat_completions", new("levels", ["high"]))
        {
            WireOptions = new Dictionary<string, JsonObject> { ["high"] = new() { ["reasoning_effort"] = "high" } },
        };
        var snapshot = new ProviderCatalogSnapshot("ok", "openai", "id", "a", choice, ["a"], [entry], false, null, _clock.GetUtcNow());
        Assert.Throws<ProviderCatalogException>(() => ProviderReasoningPreferences.Resolve(snapshot, "a", choice));
    }

    [Fact]
    public void Protocol_metadata_is_individually_bounded()
    {
        var protocol = new string('p', 65);
        var error = Assert.Throws<ProviderCatalogException>(() => ProviderCatalogParser.Parse(
            "Custom OpenAI", $"{{\"data\":[{{\"id\":\"a\",\"protocol\":\"{protocol}\"}}]}}"));
        Assert.Equal("catalog_malformed", error.Code);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
    private sealed class Credentials : ICredentialStore
    {
        public string Key { get; set; } = "synthetic-key-1";
        public List<string> Accounts { get; } = [];
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        { Accounts.Add(accountName); return Task.FromResult<string?>(Key); }
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Count;
        public string Json = "{\"data\":[{\"id\":\"model-a\"}]}";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string? Authorization;
        public TaskCompletionSource? Hold;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref Count);
            Authorization = request.Headers.Authorization?.ToString();
            Started.TrySetResult();
            if (count == 2) SecondStarted.TrySetResult();
            if (Hold is not null) await Hold.Task.WaitAsync(cancellationToken);
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Json) };
            if (Status == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new(TimeSpan.FromMinutes(2));
            return response;
        }
    }
}
