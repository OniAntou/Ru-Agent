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

    private static JsonObject Payload() => new()
    {
        ["models_dev_provider_id"] = "models.dev:candidate",
        ["models_dev_api_key"] = "synthetic-candidate",
        ["validate_models_dev_key"] = true,
    };

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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("synthetic-working", vault.Value);
            Assert.Equal(0, vault.Writes);
            Assert.Equal(ExpectedCredential, request.Headers.Authorization?.Parameter);
            BeforeReply?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("{\"data\":[{\"id\":\"model\"}]}") });
        }
    }
}
