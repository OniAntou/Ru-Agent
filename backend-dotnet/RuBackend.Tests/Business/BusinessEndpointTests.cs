using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Compatibility;
using RuBackend.Tests.Fakes;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class BusinessEndpointTests
{
    [Fact]
    public async Task GetSettings_returns_aliases_and_only_credential_presence()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"opencode\",\"opencode_model\":\"opencode/free\",\"max_tokens\":128,\"allow_web_search\":false}");
        var credentials = new FakeCredentialStore();
        credentials.Seed("mini_gpt", "api_key", "private-opencode-key");
        using var factory = CreateAspNetFactory(data.Paths, credentials: credentials);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/settings");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", payload.GetProperty("status").GetString());
        Assert.Equal("OpenCode Zen", payload.GetProperty("backend").GetString());
        Assert.Equal("opencode", payload.GetProperty("backend_choice").GetString());
        Assert.Equal(128, payload.GetProperty("max_new_tokens").GetInt32());
        Assert.False(payload.GetProperty("allow_online_search").GetBoolean());
        Assert.True(payload.GetProperty("has_opencode_key").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("opencode_api_key").GetString());
        Assert.DoesNotContain("private-opencode-key", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostSettings_loads_runtime_then_persists_without_secrets()
    {
        using var data = TemporaryBusinessData.Create();
        var credentials = new FakeCredentialStore();
        var runtime = new FakeRuntimeSettingsLoader();
        using var factory = CreateAspNetFactory(
            data.Paths,
            credentials: credentials,
            runtimeLoader: runtime);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/settings",
            new
            {
                backend_choice = "opencode",
                opencode_model = "opencode/free",
                opencode_api_key = "new-private-key",
                max_new_tokens = 192,
                allow_online_search = false,
            });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", payload.GetProperty("status").GetString());
        var loaded = Assert.Single(runtime.Candidates);
        Assert.Equal("OpenCode Zen", loaded.Backend);
        Assert.Equal("opencode/free", loaded.OpenCodeModel);
        Assert.Equal(192, loaded.MaxTokens);
        Assert.Equal(
            "new-private-key",
            await credentials.GetAsync("mini_gpt", "api_key", CancellationToken.None));
        var persisted = await File.ReadAllTextAsync(data.Paths.SettingsFile);
        Assert.DoesNotContain("new-private-key", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", persisted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_runtime_load_restores_previous_settings_and_credentials()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"llama.cpp\",\"model\":\"old.gguf\"}");
        var originalSettings = await File.ReadAllTextAsync(data.Paths.SettingsFile);
        var credentials = new FakeCredentialStore();
        credentials.Seed("mini_gpt", "api_key", "old-key");
        var runtime = new FakeRuntimeSettingsLoader
        {
            Error = new InvalidOperationException("private model path must not leak"),
        };
        using var factory = CreateAspNetFactory(
            data.Paths,
            credentials: credentials,
            runtimeLoader: runtime);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/settings",
            new
            {
                backend_choice = "opencode",
                opencode_model = "broken-model",
                opencode_api_key = "new-key",
            });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"error\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private model path", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalSettings, await File.ReadAllTextAsync(data.Paths.SettingsFile));
        Assert.Equal(
            "old-key",
            await credentials.GetAsync("mini_gpt", "api_key", CancellationToken.None));
    }

    [Fact]
    public async Task Session_routes_create_list_activate_and_delete_the_same_document()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var createdResponse = await client.PostAsync("/sessions/new", null);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = created.GetProperty("session_id").GetString()!;
        using var listResponse = await client.GetAsync("/sessions");
        var listed = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var activateResponse = await client.PostAsync(
            $"/sessions/{sessionId}/activate",
            null);
        var activated = await activateResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var deleteResponse = await client.DeleteAsync($"/sessions/{sessionId}");
        using var missingResponse = await client.PostAsync(
            $"/sessions/{sessionId}/activate",
            null);
        var missing = await missingResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ok", created.GetProperty("status").GetString());
        Assert.Equal(sessionId, created.GetProperty("session").GetProperty("id").GetString());
        Assert.Equal(sessionId, listed.GetProperty("sessions")[0].GetProperty("id").GetString());
        Assert.Equal(sessionId, activated.GetProperty("session").GetProperty("id").GetString());
        Assert.Equal("ok", (await deleteResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString());
        Assert.Equal("error", missing.GetProperty("status").GetString());
    }

    [Fact]
    public async Task History_and_activity_routes_preserve_existing_response_shapes()
    {
        using var data = TemporaryBusinessData.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(data.Paths.HistoryFile)!);
        await File.WriteAllTextAsync(
            data.Paths.HistoryFile,
            "[{\"role\":\"user\",\"content\":\"Legacy prompt\"}]");
        await File.WriteAllTextAsync(
            data.Paths.ActivityLogFile,
            "{\"ts\":1,\"iso\":\"1970-01-01T00:00:01+00:00\",\"type\":\"synthetic\",\"session_id\":null,\"data\":{\"count\":1}}\n");
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        var history = await client.GetFromJsonAsync<JsonElement>("/history");
        var activity = await client.GetFromJsonAsync<JsonElement>("/activity-log?limit=200");
        using var cleared = await client.DeleteAsync("/activity-log");

        Assert.Equal(JsonValueKind.Array, history.ValueKind);
        Assert.Equal("Legacy prompt", history[0].GetProperty("content").GetString());
        Assert.Equal("ok", activity.GetProperty("status").GetString());
        Assert.Equal("synthetic", activity.GetProperty("events")[0].GetProperty("type").GetString());
        Assert.Equal("ok", (await cleared.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString());
        Assert.False(File.Exists(data.Paths.ActivityLogFile));
    }

    [Fact]
    public async Task Python_ownership_forwards_business_routes_unchanged()
    {
        const string body = "{\"status\":\"ok\",\"sessions\":[{\"id\":\"python\"}]}";
        var transport = new StubUpstreamHttpTransport(new UpstreamResponse(
            StatusCodes.Status200OK,
            "application/json",
            Encoding.UTF8.GetBytes(body)));
        using var factory = new BackendWebApplicationFactory(
            httpTransport: transport,
            settingsOwnership: "python",
            sessionsOwnership: "python",
            activityOwnership: "python");
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/sessions");

        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal("/sessions", transport.LastRequest!.PathAndQuery);
    }

    [Fact]
    public async Task Shadow_read_returns_Python_body_and_reads_local_store_without_writing()
    {
        const string body = "{\"status\":\"ok\",\"backend_choice\":\"gguf\"}";
        var transport = new StubUpstreamHttpTransport(new UpstreamResponse(
            StatusCodes.Status200OK,
            "application/json",
            Encoding.UTF8.GetBytes(body)));
        var settings = new CountingSettingsStore();
        using var factory = new BackendWebApplicationFactory(
            httpTransport: transport,
            settingsStore: settings,
            credentialStore: new FakeCredentialStore(),
            settingsOwnership: "shadow",
            sessionsOwnership: "python",
            activityOwnership: "python");
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/settings");

        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, settings.ReadCount);
        Assert.Equal(0, settings.SaveCount);
    }

    private static BackendWebApplicationFactory CreateAspNetFactory(
        DataPaths paths,
        ICredentialStore? credentials = null,
        IRuntimeSettingsLoader? runtimeLoader = null) =>
        new(
            dataPaths: paths,
            credentialStore: credentials ?? new FakeCredentialStore(),
            runtimeSettingsLoader: runtimeLoader ?? new FakeRuntimeSettingsLoader(),
            settingsOwnership: "aspnet",
            sessionsOwnership: "aspnet",
            activityOwnership: "aspnet");

    private sealed class FakeRuntimeSettingsLoader : IRuntimeSettingsLoader
    {
        public List<SettingsSnapshot> Candidates { get; } = [];

        public Exception? Error { get; init; }

        public Task<string> LoadAsync(
            SettingsSnapshot candidate,
            CancellationToken cancellationToken)
        {
            Candidates.Add(candidate);
            return Error is null
                ? Task.FromResult("Runtime loaded.")
                : Task.FromException<string>(Error);
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<(string Service, string Account), string> _values = [];

        public void Seed(string service, string account, string value) =>
            _values[(service, account)] = value;

        public Task<string?> GetAsync(
            string serviceName,
            string accountName,
            CancellationToken cancellationToken) =>
            Task.FromResult(_values.GetValueOrDefault((serviceName, accountName)));

        public Task SetAsync(
            string serviceName,
            string accountName,
            string secret,
            CancellationToken cancellationToken)
        {
            _values[(serviceName, accountName)] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            string serviceName,
            string accountName,
            CancellationToken cancellationToken)
        {
            _values.Remove((serviceName, accountName));
            return Task.CompletedTask;
        }
    }

    private sealed class CountingSettingsStore : ISettingsStore
    {
        public int ReadCount { get; private set; }
        public int SaveCount { get; private set; }

        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(new SettingsSnapshot(
                [],
                "llama.cpp",
                string.Empty,
                string.Empty,
                string.Empty,
                "Default",
                256,
                0.7,
                50,
                true,
                true,
                true,
                false,
                string.Empty,
                "Ru (OmniVoice)",
                "original",
                1.0,
                false,
                true,
                false));
        }

        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryBusinessData : IDisposable
    {
        private TemporaryBusinessData(string root, DataPaths paths)
        {
            Root = root;
            Paths = paths;
        }

        public string Root { get; }

        public DataPaths Paths { get; }

        public static TemporaryBusinessData Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ru-business-endpoints-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TemporaryBusinessData(
                root,
                new DataPaths(
                    Path.Combine(root, "memory.db"),
                    Path.Combine(root, "memory_texts.json"),
                    Path.Combine(root, "settings.json"),
                    Path.Combine(root, "history.json"),
                    Path.Combine(root, "sessions"),
                    Path.Combine(root, "activity.jsonl")));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
