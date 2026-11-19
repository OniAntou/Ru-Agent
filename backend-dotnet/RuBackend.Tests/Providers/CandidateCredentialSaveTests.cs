using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class CandidateCredentialSaveTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, true, null)]
    [InlineData(HttpStatusCode.Unauthorized, false, "provider_key_rejected")]
    [InlineData(HttpStatusCode.Forbidden, false, "provider_key_rejected")]
    [InlineData(HttpStatusCode.TooManyRequests, false, "provider_key_unverified")]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, "provider_key_unverified")]
    public async Task Candidate_is_checked_before_any_credential_write(HttpStatusCode status, bool success, string? code)
    {
        using var fixture = new Fixture(status);
        var result = await fixture.Service.ApplyAsync(Payload(), default);
        Assert.Equal(success, result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal(success ? "synthetic-candidate" : "synthetic-working", fixture.Credentials.Value);
        Assert.Equal(success ? 1 : 0, fixture.Credentials.Writes);
        Assert.Equal("custom", fixture.Settings.Snapshot.Backend);
    }

    [Fact]
    public async Task Cancelled_validation_preserves_the_previous_credential()
    {
        using var fixture = new Fixture(HttpStatusCode.OK);
        using var cancel = new CancellationTokenSource();
        fixture.Handler.BeforeReply = () => cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ApplyAsync(Payload(), cancel.Token));
        Assert.Equal("synthetic-working", fixture.Credentials.Value);
        Assert.Equal(0, fixture.Credentials.Writes);
    }

    [Fact]
    public async Task Settings_failure_restores_previous_credential_after_a_valid_probe()
    {
        using var fixture = new Fixture(HttpStatusCode.OK);
        fixture.Settings.FailSave = true;
        Assert.False((await fixture.Service.ApplyAsync(Payload(), default)).Success);
        Assert.Equal("synthetic-working", fixture.Credentials.Value);
    }

    [Fact]
    public async Task Validation_rejects_mixed_settings_writes_without_sending_a_key()
    {
        using var fixture = new Fixture(HttpStatusCode.OK);
        var payload = Payload();
        payload["backend_choice"] = "models.dev:candidate";
        Assert.False((await fixture.Service.ApplyAsync(payload, default)).Success);
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Equal(0, fixture.Credentials.Writes);
    }

    private static JsonObject Payload(string provider = "models.dev:candidate", string key = "synthetic-candidate") => new()
    {
        ["models_dev_provider_id"] = provider,
        ["models_dev_api_key"] = key,
        ["validate_provider_key"] = true,
    };

    [Fact]
    public async Task A_catalog_that_answers_without_a_key_saves_the_key_as_unchecked()
    {
        using var fixture = new Fixture(HttpStatusCode.OK);
        fixture.Handler.AnonymousStatus = HttpStatusCode.OK;
        var result = await fixture.Service.ApplyAsync(Payload(), default);
        Assert.True(result.Success);
        Assert.Equal("provider_key_unchecked", result.Code);
        Assert.Equal("synthetic-candidate", fixture.Credentials.Value);
        Assert.Equal(1, fixture.Handler.AnonymousCalls);
    }

    [Fact]
    public async Task A_catalog_that_refuses_an_anonymous_request_proves_the_key()
    {
        using var fixture = new Fixture(HttpStatusCode.OK);
        var result = await fixture.Service.ApplyAsync(Payload(), default);
        Assert.True(result.Success);
        Assert.Null(result.Code);
        Assert.Equal(1, fixture.Handler.AnonymousCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true, null)]
    [InlineData(HttpStatusCode.Unauthorized, false, "provider_key_rejected")]
    [InlineData(HttpStatusCode.BadRequest, false, "provider_key_rejected")]
    [InlineData(HttpStatusCode.Forbidden, true, "provider_key_unchecked")]
    [InlineData(HttpStatusCode.InternalServerError, false, "provider_key_unverified")]
    public async Task OpenRouter_keys_are_checked_on_its_authenticated_key_endpoint(HttpStatusCode status, bool success, string? code)
    {
        using var fixture = new Fixture(status);
        fixture.Handler.AnonymousStatus = HttpStatusCode.OK;
        var result = await fixture.Service.ApplyAsync(Payload("models.dev:openrouter"), default);
        Assert.Equal(success, result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal([new Uri("https://openrouter.ai/api/v1/key")], fixture.Handler.Uris);
        Assert.Equal(0, fixture.Handler.AnonymousCalls);
        Assert.Equal(success ? "synthetic-candidate" : "synthetic-working", fixture.Credentials.Value);
    }

    [Fact]
    public async Task Cloudflare_keys_are_sent_to_the_account_instead_of_skipped()
    {
        using var fixture = new Fixture(HttpStatusCode.Unauthorized);
        fixture.Settings.Snapshot = fixture.Settings.Snapshot with { CloudflareAccountId = "abc123" };
        var result = await fixture.Service.ApplyAsync(Payload("models.dev:cloudflare-workers-ai"), default);
        Assert.False(result.Success);
        Assert.Equal("provider_key_rejected", result.Code);
        Assert.Equal(
            [new Uri("https://api.cloudflare.com/client/v4/accounts/abc123/ai/models/search?per_page=1")],
            fixture.Handler.Uris);
        Assert.Equal(0, fixture.Credentials.Writes);
    }

    [Theory]
    [InlineData("openai_api_key", "https://api.openai.com/v1/models")]
    [InlineData("anthropic_api_key", "https://api.anthropic.com/v1/models")]
    public async Task Fixed_provider_keys_are_checked_before_they_are_stored(string field, string uri)
    {
        using var fixture = new Fixture(HttpStatusCode.Unauthorized);
        var payload = new JsonObject { [field] = "synthetic-candidate", ["validate_provider_key"] = true };
        var result = await fixture.Service.ApplyAsync(payload, default);
        Assert.False(result.Success);
        Assert.Equal("provider_key_rejected", result.Code);
        Assert.Equal([new Uri(uri)], fixture.Handler.Uris);
        Assert.Equal(0, fixture.Credentials.Writes);
    }

    [Theory]
    [InlineData("synthetic\n")]
    [InlineData("synthetic\r\nkey")]
    [InlineData("synthetic\u0000")]
    public async Task Keys_no_header_can_carry_are_refused_by_name(string escaped)
    {
        using var fixture = new Fixture(HttpStatusCode.OK);
        var key = System.Text.RegularExpressions.Regex.Unescape(escaped);
        var result = await fixture.Service.ApplyAsync(Payload(key: key), default);
        Assert.False(result.Success);
        Assert.Equal("provider_key_malformed", result.Code);
        Assert.DoesNotContain(key, result.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Equal(0, fixture.Credentials.Writes);
    }

    [Fact]
    public async Task A_key_longer_than_the_vault_holds_is_refused_by_name()
    {
        using var fixture = new Fixture(HttpStatusCode.OK);
        var key = new string('k', WindowsCredentialStore.MaximumSecretCharacters + 1);
        var result = await fixture.Service.ApplyAsync(Payload(key: key), default);
        Assert.False(result.Success);
        Assert.Equal("provider_key_too_long", result.Code);
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Equal(0, fixture.Credentials.Writes);
    }

    [Fact]
    public async Task A_saved_key_is_redacted_from_runs_without_a_restart()
    {
        using var fixture = new Fixture(HttpStatusCode.OK);
        fixture.Handler.ExpectedCredential = "synthetic-candidate-key";
        var secrets = new RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor([]);
        var service = new SettingsTransitionService(fixture.Settings, fixture.Credentials, fixture.Catalog, secrets);
        Assert.True((await service.ApplyAsync(Payload(key: "synthetic-candidate-key"), default)).Success);
        Assert.Equal(
            RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor.Replacement,
            secrets.Redact("synthetic-candidate-key"));
    }

    [Fact]
    public async Task A_refused_key_is_not_seeded()
    {
        using var fixture = new Fixture(HttpStatusCode.Unauthorized);
        var secrets = new RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor([]);
        var service = new SettingsTransitionService(fixture.Settings, fixture.Credentials, fixture.Catalog, secrets);
        Assert.False((await service.ApplyAsync(Payload(), default)).Success);
        Assert.Equal("synthetic-candidate", secrets.Redact("synthetic-candidate"));
    }

    [Fact]
    public async Task Validation_refuses_a_key_mixed_with_an_endpoint_change()
    {
        using var fixture = new Fixture(HttpStatusCode.OK);
        var payload = new JsonObject
        {
            ["custom_api_key"] = "synthetic-candidate",
            ["custom_url"] = "https://elsewhere.example/v1",
            ["validate_provider_key"] = true,
        };
        Assert.Equal("settings_invalid", (await fixture.Service.ApplyAsync(payload, default)).Code);
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Equal(0, fixture.Credentials.Writes);
    }

    [Fact]
    public async Task Rejected_candidate_does_not_replace_the_live_catalog_cache()
    {
        using var fixture = new Fixture(HttpStatusCode.OK);
        fixture.Handler.ExpectedCredential = "synthetic-working";
        var before = await fixture.Catalog.GetAsync("models.dev:candidate", fixture.Settings.Snapshot, false, default);
        fixture.Handler.ExpectedCredential = "synthetic-candidate";
        fixture.Handler.Status = HttpStatusCode.Unauthorized;
        Assert.False((await fixture.Service.ApplyAsync(Payload(), default)).Success);
        var calls = fixture.Handler.Calls;
        var after = await fixture.Catalog.GetAsync("models.dev:candidate", fixture.Settings.Snapshot, false, default);
        Assert.Equal(before.Identity, after.Identity);
        Assert.Equal("ok", after.Status);
        Assert.Equal(calls, fixture.Handler.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-candidate-test-" + Guid.NewGuid().ToString("N"));
        public readonly Vault Credentials = new();
        public readonly Store Settings = new();
        public readonly Handler Handler;
        public readonly SettingsTransitionService Service;
        public readonly ProviderCatalogService Catalog;
        private readonly HttpClient _http;
        public Fixture(HttpStatusCode status)
        {
            Directory.CreateDirectory(_root);
            var cache = Path.Combine(_root, "models-dev.json");
            var entries = Enumerable.Range(0, 60).ToDictionary(i => "provider" + i, _ => new { api = "https://candidate.example/v1" });
            entries["candidate"] = new { api = "https://candidate.example/v1" };
            entries["openrouter"] = new { api = "https://openrouter.ai/api/v1" };
            entries["cloudflare-workers-ai"] = new { api = "https://api.cloudflare.com/client/v4/accounts/${CLOUDFLARE_ACCOUNT_ID}/ai/v1" };
            File.WriteAllText(cache, JsonSerializer.Serialize(new { fetched_at = DateTimeOffset.UtcNow, payload = entries }));
            Handler = new(status, Credentials);
            _http = new HttpClient(Handler);
            Catalog = new(_http, Credentials, TimeProvider.System, cache);
            Service = new(Settings, Credentials, Catalog);
        }
        public void Dispose()
        {
            _http.Dispose();
            if (!Path.GetFullPath(_root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(_root, true);
        }
    }
    private sealed class Vault : ICredentialStore
    {
        public string Value = "synthetic-working";
        public int Writes;
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) => Task.FromResult<string?>(Value);
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) { Writes++; Value = secret; return Task.CompletedTask; }
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) => throw new InvalidOperationException("Must never delete");
    }
    private sealed class Store : ISettingsStore
    {
        public SettingsSnapshot Snapshot = new(new JsonObject(), "custom", "model", "default", "http://localhost:8000/v1", "", "");
        public bool FailSave;
        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken)
        {
            if (FailSave) throw new IOException("Synthetic failure");
            Snapshot = candidate;
            return Task.CompletedTask;
        }
    }
    private sealed class Handler(HttpStatusCode status, Vault vault) : HttpMessageHandler
    {
        public int Calls;
        public HttpStatusCode Status = status;
        public string ExpectedCredential = "synthetic-candidate";
        public Action? BeforeReply;
        /// <summary>What the catalog answers a request that carries no key.</summary>
        public HttpStatusCode AnonymousStatus = HttpStatusCode.Unauthorized;
        public int AnonymousCalls;
        public readonly List<Uri> Uris = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uris.Add(request.RequestUri!);
            Assert.Equal("synthetic-working", vault.Value);
            Assert.Equal(0, vault.Writes);
            if (request.Headers.Authorization is null && request.Headers.All(h => h.Key != "x-api-key"))
            {
                AnonymousCalls++;
                return Task.FromResult(new HttpResponseMessage(AnonymousStatus) { Content = new StringContent("{\"data\":[{\"id\":\"model\"}]}") });
            }
            Assert.Equal(ExpectedCredential, request.Headers.Authorization?.Parameter
                ?? request.Headers.GetValues("x-api-key").Single());
            BeforeReply?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("{\"data\":[{\"id\":\"model\"}]}") });
        }
    }
}
