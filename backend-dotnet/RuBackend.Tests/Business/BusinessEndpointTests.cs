using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuBackend.Business;
using RuBackend.Business.Settings;
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
        credentials.Seed("Ru", "api_key", "private-opencode-key");
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
            await credentials.GetAsync("Ru", "api_key", CancellationToken.None));
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
        credentials.Seed("Ru", "api_key", "old-key");
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
            await credentials.GetAsync("Ru", "api_key", CancellationToken.None));
    }

    [Fact]
    public async Task PostSettings_with_gguf_payload_migrates_to_opencode()
    {
        using var data = TemporaryBusinessData.Create();
        var runtime = new FakeRuntimeSettingsLoader();
        using var factory = CreateAspNetFactory(
            data.Paths,
            runtimeLoader: runtime);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/settings",
            new
            {
                backend_choice = "gguf",
                model = "LFM2.5-2.6B-Q4_K_M.gguf",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("OpenCode Zen", Assert.Single(runtime.Candidates).Backend);
    }

    [Fact]
    public async Task PostSettings_saves_ollama_backend_with_model_and_url()
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
                backend_choice = "ollama",
                ollama_model = "qwen3:8b",
                ollama_url = "http://127.0.0.1:11434/v1",
                max_new_tokens = 192,
            });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", payload.GetProperty("status").GetString());
        var loaded = Assert.Single(runtime.Candidates);
        Assert.Equal("Ollama", loaded.Backend);
        Assert.Equal("qwen3:8b", loaded.OllamaModel);
        Assert.Equal("http://127.0.0.1:11434/v1", loaded.OllamaUrl);
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(data.Paths.SettingsFile))!.AsObject();
        Assert.Equal("Ollama", persisted["backend"]!.GetValue<string>());
        Assert.Equal("qwen3:8b", persisted["ollama_model"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostSettings_custom_backend_stores_key_as_credential_and_never_in_file()
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
                backend_choice = "custom",
                custom_model = "my-model",
                custom_url = "http://localhost:9000/v1",
                custom_api_key = "private-custom-key",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "private-custom-key",
            await credentials.GetAsync("Ru", "custom_api_key", CancellationToken.None));
        var persisted = await File.ReadAllTextAsync(data.Paths.SettingsFile);
        Assert.DoesNotContain("private-custom-key", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("custom_api_key", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Custom OpenAI", Assert.Single(runtime.Candidates).Backend);
    }

    [Fact]
    public async Task GetSettings_returns_the_new_backend_fields_and_custom_key_presence()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"ollama\",\"ollama_model\":\"qwen3:8b\",\"ollama_url\":\"http://127.0.0.1:11434/v1\",\"custom_url\":\"http://localhost:9000/v1\"}");
        var credentials = new FakeCredentialStore();
        credentials.Seed("Ru", "custom_api_key", "private-custom-key");
        using var factory = CreateAspNetFactory(data.Paths, credentials: credentials);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/settings");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ollama", payload.GetProperty("backend_choice").GetString());
        Assert.Equal("qwen3:8b", payload.GetProperty("ollama_model").GetString());
        Assert.Equal("http://127.0.0.1:11434/v1", payload.GetProperty("ollama_url").GetString());
        Assert.Equal("http://localhost:9000/v1", payload.GetProperty("custom_url").GetString());
        Assert.True(payload.GetProperty("has_custom_api_key").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("custom_api_key").GetString());
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

    private static BackendWebApplicationFactory CreateAspNetFactory(
        DataPaths paths,
        ICredentialStore? credentials = null,
        IRuntimeSettingsLoader? runtimeLoader = null) =>
        new(
            dataPaths: paths,
            credentialStore: credentials ?? new FakeCredentialStore(),
            runtimeSettingsLoader: runtimeLoader ?? new FakeRuntimeSettingsLoader());

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
