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

    [Fact]
    public async Task Cache_refreshes_on_expiry_and_throttles_manual_requests()
    {
        var service = Service();
        var settings = await SettingsAsync();
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
        Assert.Equal("catalog_malformed", Assert.Throws<ProviderCatalogException>(() => ProviderCatalogParser.Parse("OpenCode Zen", json)).Code);

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
    public void Kilo_variants_are_allowlisted_and_never_copy_arbitrary_request_fields()
    {
        var models = ProviderCatalogParser.Parse("Kilo Gateway", """
          {"data":[{"id":"a","opencode":{"variants":{
            "high":{"reasoning":{"enabled":true,"effort":"high"},"model":"evil"},
            "max":{"reasoning":{"effort":"max","unexpected":"evil"}}
          }}}]}
          """);
        var model = Assert.Single(models);
        Assert.Equal(new[] { "high" }, model.Reasoning.Options);
        Assert.Equal("{\"reasoning\":{\"enabled\":true,\"effort\":\"high\"}}", model.WireOptions["high"].ToJsonString());
    }

    [Fact]
    public void Bare_ids_remain_unknown_and_other_protocols_are_not_runnable()
    {
        var models = ProviderCatalogParser.Parse("OpenCode Zen", """
          {"data":[{"id":"deepseek-v4-flash-free"},{"id":"gpt-5.4"},{"id":"claude-opus-4.6"}]}
          """);
        Assert.Equal("unknown", models[0].Reasoning.State);
        Assert.Empty(models[0].Reasoning.Options);
        Assert.False(models[1].Supported);
        Assert.False(models[2].Supported);
    }

    [Fact]
    public async Task Preferences_are_per_model_and_default_omits_options()
    {
        var settings = (await SettingsAsync()) with { OpenCodeModel = "a" };
        var saved = ProviderReasoningPreferences.Save(settings, "high");
        Assert.Equal("high", ProviderReasoningPreferences.Read(saved));
        Assert.Equal("default", ProviderReasoningPreferences.Read(saved with { OpenCodeModel = "b" }));
        var entry = new CatalogModel("a", true, "chat_completions", new("unknown", []));
        var snapshot = new ProviderCatalogSnapshot("ok", "opencode", "id", "a", "default", ["a"], [entry], false, null, _clock.GetUtcNow());
        Assert.Empty(ProviderReasoningPreferences.Resolve(snapshot, "a", "default"));
        Assert.Throws<ProviderCatalogException>(() => ProviderReasoningPreferences.Resolve(snapshot, "a", "high"));
    }

    [Fact]
    public async Task Empty_selected_model_is_not_fabricated_from_a_provider_default()
    {
        var settings = (await SettingsAsync()) with
        {
            Model = string.Empty,
            OpenCodeModel = string.Empty,
        };

        var selection = ProviderSelection.From(settings);

        Assert.Equal(string.Empty, selection.Model);
    }

    [Theory]
    [InlineData("OpenCode Zen", "api_key")]
    [InlineData("Kilo Gateway", "kilo_gateway_api_key")]
    [InlineData("Custom OpenAI", "custom_api_key")]
    [InlineData("Ollama", null)]
    [InlineData("LM Studio", null)]
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
        var settings = (await SettingsAsync()) with { OpenCodeModel = "a" };
        var key = ProviderSelection.From(settings).PreferenceKey;
        settings.Raw["reasoning_preferences"] = new JsonObject { [key] = JsonNode.Parse(malformed) };

        var choice = ProviderReasoningPreferences.Read(settings);

        Assert.Equal(ProviderReasoningPreferences.InvalidChoice, choice);
        var entry = new CatalogModel("a", true, "chat_completions", new("levels", ["high"]))
        {
            WireOptions = new Dictionary<string, JsonObject> { ["high"] = new() { ["reasoning_effort"] = "high" } },
        };
        var snapshot = new ProviderCatalogSnapshot("ok", "opencode", "id", "a", choice, ["a"], [entry], false, null, _clock.GetUtcNow());
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
