using System.Net;
using System.Text.Json.Nodes;
using RuAgent.Harness.Persistence;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

/// <summary>
/// Custom endpoint key checks and the stored per-provider key status (provider_key_status).
/// Everything runs against in-memory settings, an in-memory vault and a stub handler.
/// </summary>
public sealed class ProviderKeyStatusTests
{
    private const string LegacyUrl = "https://lab.example/v1";

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, null, false, "provider_key_rejected", null)]
    [InlineData(HttpStatusCode.NotFound, null, true, "provider_key_unchecked", ProviderKeyStatus.Unchecked)]
    [InlineData(HttpStatusCode.Forbidden, null, true, "provider_key_unchecked", ProviderKeyStatus.Unchecked)]
    [InlineData(HttpStatusCode.OK, HttpStatusCode.Unauthorized, true, null, ProviderKeyStatus.Verified)]
    [InlineData(HttpStatusCode.OK, HttpStatusCode.OK, true, "provider_key_unchecked", ProviderKeyStatus.Unchecked)]
    public async Task A_custom_endpoint_refuses_only_a_401_and_saves_everything_short_of_proof(
        HttpStatusCode keyed, HttpStatusCode? anonymous, bool saved, string? code, string? status)
    {
        var fixture = new Fixture(new Handler { Keyed = keyed, Anonymous = anonymous ?? HttpStatusCode.OK });
        var result = await fixture.Service.ApplyAsync(
            new JsonObject { ["custom_api_key"] = "synthetic-custom-key", [SettingsTransitionService.ValidateKeyField] = true }, default);

        Assert.Equal(saved, result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal(saved ? "synthetic-custom-key" : null, fixture.Vault.Get("custom_api_key"));
        Assert.Equal(status, ProviderKeyStatus.Read(fixture.Settings.Snapshot, "Custom OpenAI"));
        Assert.All(fixture.Handler.Uris, uri => Assert.Equal(new Uri(LegacyUrl + "/models"), uri));
    }

    [Fact]
    public async Task An_unreachable_custom_server_still_takes_the_key_unchecked()
    {
        var fixture = new Fixture(new Handler { Unreachable = true });
        var result = await fixture.Service.ApplyAsync(
            new JsonObject { ["custom_api_key"] = "synthetic-custom-key", [SettingsTransitionService.ValidateKeyField] = true }, default);
        Assert.True(result.Success);
        Assert.Equal("provider_key_unchecked", result.Code);
    }

    [Fact]
    public async Task A_named_custom_provider_key_is_checked_against_its_saved_endpoint()
    {
        var fixture = new Fixture(new Handler { Keyed = HttpStatusCode.Unauthorized });
        fixture.Settings.Snapshot.Raw[CustomProviders.SettingField] = new JsonArray(new JsonObject
        {
            ["id"] = "lab",
            ["display_name"] = "Lab",
            ["base_url"] = "https://lab-two.example/v1",
            ["protocol"] = "chat_completions",
            ["models"] = new JsonArray(),
        });
        var result = await fixture.Service.ApplyAsync(new JsonObject
        {
            ["custom_provider_id"] = "lab",
            ["custom_provider_api_key"] = "synthetic-custom-key",
            [SettingsTransitionService.ValidateKeyField] = true,
        }, default);

        Assert.Equal("provider_key_rejected", result.Code);
        Assert.Equal([new Uri("https://lab-two.example/v1/models")], fixture.Handler.Uris);
        Assert.Null(fixture.Vault.Get(CustomProviders.CredentialAccount("lab")));
    }

    [Fact]
    public async Task A_settings_payload_cannot_write_the_status()
    {
        var fixture = new Fixture(new Handler());
        var result = await fixture.Service.ApplyAsync(new JsonObject
        {
            [ProviderKeyStatus.SettingField] = new JsonObject { ["OpenAI"] = new JsonObject { ["state"] = "verified" } },
        }, default);
        Assert.True(result.Success);
        Assert.Null(ProviderKeyStatus.Read(fixture.Settings.Snapshot, "OpenAI"));
    }

    [Fact]
    public async Task Moving_the_endpoint_or_signing_out_forgets_the_verdict()
    {
        var fixture = new Fixture(new Handler());
        fixture.Vault.Set("custom_api_key", "synthetic-custom-key");
        ProviderKeyStatus.Set(fixture.Settings.Snapshot.Raw, "Custom OpenAI", ProviderKeyStatus.Verified, DateTimeOffset.UtcNow);
        ProviderKeyStatus.Set(fixture.Settings.Snapshot.Raw, "OpenAI", ProviderKeyStatus.Verified, DateTimeOffset.UtcNow);
        fixture.Vault.Set("openai_api_key", "synthetic-openai-key");

        Assert.True((await fixture.Service.ApplyAsync(new JsonObject { ["custom_url"] = "https://elsewhere.example/v1" }, default)).Success);
        Assert.Null(ProviderKeyStatus.Read(fixture.Settings.Snapshot, "Custom OpenAI"));
        Assert.Equal(ProviderKeyStatus.Verified, ProviderKeyStatus.Read(fixture.Settings.Snapshot, "OpenAI"));

        Assert.True((await fixture.Service.RemoveProviderAsync("openai", default)).Success);
        Assert.Null(ProviderKeyStatus.Read(fixture.Settings.Snapshot, "OpenAI"));
    }

    [Fact]
    public async Task A_runs_verdict_lands_only_on_the_key_and_endpoint_it_used()
    {
        var fixture = new Fixture(new Handler());
        fixture.Vault.Set("openai_api_key", "synthetic-used-key");

        await fixture.Service.RecordKeyStatusAsync("OpenAI", "openai_api_key", "synthetic-used-key",
            ProviderKeyStatus.Rejected, _ => true, default);
        Assert.Equal(ProviderKeyStatus.Rejected, ProviderKeyStatus.Read(fixture.Settings.Snapshot, "OpenAI"));
        var saves = fixture.Settings.Saves;

        // The same state again writes nothing.
        await fixture.Service.RecordKeyStatusAsync("OpenAI", "openai_api_key", "synthetic-used-key",
            ProviderKeyStatus.Rejected, _ => true, default);
        Assert.Equal(saves, fixture.Settings.Saves);

        // A key replaced since the run started keeps its own status.
        fixture.Vault.Set("openai_api_key", "synthetic-new-key");
        await fixture.Service.RecordKeyStatusAsync("OpenAI", "openai_api_key", "synthetic-used-key",
            ProviderKeyStatus.Verified, _ => true, default);
        Assert.Equal(ProviderKeyStatus.Rejected, ProviderKeyStatus.Read(fixture.Settings.Snapshot, "OpenAI"));

        // So does an endpoint the user moved.
        await fixture.Service.RecordKeyStatusAsync("OpenAI", "openai_api_key", "synthetic-new-key",
            ProviderKeyStatus.Verified, _ => false, default);
        Assert.Equal(ProviderKeyStatus.Rejected, ProviderKeyStatus.Read(fixture.Settings.Snapshot, "OpenAI"));
    }

    [Theory]
    [InlineData("https://lab.example/v1", "https://lab.example/v1/", true)]
    [InlineData("https://lab.example/v1", "https://other.example/v1", false)]
    public void A_legacy_custom_pin_matches_only_the_url_it_ran_against(string stored, string pinned, bool expected)
    {
        var settings = new SettingsSnapshot(new JsonObject(), "Custom OpenAI", "m", "m", stored, "", "");
        var pin = new AgentRunProviderSelection("Custom OpenAI", "custom", ProviderProtocols.ChatCompletions, pinned, "m",
            "custom_api_key", "{}", null, "m", "native");
        Assert.Equal(expected, ProviderKeyStatusRecorder.EndpointUnchanged(settings, pin));
    }

    [Theory]
    [InlineData("custom_api_key", "Custom OpenAI")]
    [InlineData("openai_api_key", "OpenAI")]
    [InlineData("openrouter_api_key", "models.dev:openrouter")]
    [InlineData("custom_provider_lab_key", "custom:lab")]
    [InlineData("nothing", null)]
    public void Every_credential_account_names_its_backend(string account, string? backend) =>
        Assert.Equal(backend, ProviderKeyStatus.BackendForAccount(account));

    private sealed class Fixture
    {
        public readonly Store Settings = new();
        public readonly Vault Vault = new();
        public readonly Handler Handler;
        public readonly SettingsTransitionService Service;

        public Fixture(Handler handler)
        {
            Handler = handler;
            var catalog = new ProviderCatalogService(new HttpClient(handler), Vault, TimeProvider.System);
            Service = new SettingsTransitionService(Settings, Vault, catalog);
        }
    }

    internal sealed class Store : ISettingsStore
    {
        public SettingsSnapshot Snapshot = new(new JsonObject(), "Custom OpenAI", "m", "m", LegacyUrl, "", "");
        public int Saves;
        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken)
        {
            Saves++;
            Snapshot = candidate;
            return Task.CompletedTask;
        }
    }

    internal sealed class Vault : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string account) => _values.GetValueOrDefault(account);
        public void Set(string account, string value) => _values[account] = value;
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            Task.FromResult(Get(accountName));
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken)
        {
            Set(accountName, secret);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        {
            _values.Remove(accountName);
            return Task.CompletedTask;
        }
    }

    internal sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Keyed = HttpStatusCode.OK;
        public HttpStatusCode Anonymous = HttpStatusCode.OK;
        public bool Unreachable;
        public readonly List<Uri> Uris = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            if (Unreachable) throw new HttpRequestException("synthetic connection refused");
            var status = request.Headers.Authorization is null ? Anonymous : Keyed;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"data\":[]}") });
        }
    }
}
