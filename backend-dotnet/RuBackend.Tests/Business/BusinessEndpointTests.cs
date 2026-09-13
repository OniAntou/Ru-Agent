using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Conversation;
using RuBackend.Tests.Fakes;
using RuBackend.Worker;
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
        Assert.DoesNotContain("max_tokens", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("max_new_tokens", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("temperature", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("top_k", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_web_search", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_online_search", raw, StringComparison.Ordinal);
        Assert.True(payload.GetProperty("has_opencode_key").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("opencode_api_key").GetString());
        Assert.DoesNotContain("private-opencode-key", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostSettingsProviderRemove_deletes_only_the_named_provider_credential()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"opencode\",\"opencode_model\":\"opencode/free\"}");
        var credentials = new FakeCredentialStore();
        credentials.Seed("Ru", "api_key", "private-opencode-key");
        credentials.Seed("Ru", "openai_api_key", "private-openai-key");
        using var factory = CreateAspNetFactory(data.Paths, credentials: credentials);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/settings/provider/remove",
            new { backend_choice = "opencode" });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", payload.GetProperty("status").GetString());

        using var settings = await client.GetAsync("/settings");
        var raw = await settings.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(raw)!;
        Assert.False(body["has_opencode_key"]!.GetValue<bool>());
        Assert.True(body["has_openai_api_key"]!.GetValue<bool>());
        Assert.DoesNotContain("private-opencode-key", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("private-openai-key", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostSettingsProviderRemove_is_idempotent_and_rejects_unknown_alias()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"opencode\",\"opencode_model\":\"opencode/free\"}");
        var credentials = new FakeCredentialStore();
        credentials.Seed("Ru", "api_key", "private-opencode-key");
        using var factory = CreateAspNetFactory(data.Paths, credentials: credentials);
        using var client = factory.CreateAuthenticatedClient();

        using var unknown = await client.PostAsJsonAsync(
            "/settings/provider/remove",
            new { backend_choice = "banana" });
        Assert.Equal(
            "error",
            (await unknown.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("status").GetString());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var removed = await client.PostAsJsonAsync(
                "/settings/provider/remove",
                new { backend_choice = "opencode" });
            Assert.Equal(
                "success",
                (await removed.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task GetSettings_unknown_persisted_backend_never_reports_opencode()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(data.Paths.SettingsFile, "{\"backend\":\"future-provider\"}");
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/settings");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, payload.GetProperty("backend").GetString());
        Assert.Equal(string.Empty, payload.GetProperty("backend_choice").GetString());
    }

    [Fact]
    public async Task PostSettings_persists_without_secrets()
    {
        using var data = TemporaryBusinessData.Create();
        var credentials = new FakeCredentialStore();
        using var factory = CreateAspNetFactory(
            data.Paths,
            credentials: credentials);
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
        Assert.Equal(
            "new-private-key",
            await credentials.GetAsync("Ru", "api_key", CancellationToken.None));
        var persisted = await File.ReadAllTextAsync(data.Paths.SettingsFile);
        Assert.Contains("opencode/free", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("new-private-key", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max_tokens", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("max_new_tokens", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("temperature", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("top_k", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_web_search", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_online_search", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostSettings_with_gguf_payload_migrates_to_opencode()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/settings",
            new
            {
                backend_choice = "gguf",
                model = "LFM2.5-2.6B-Q4_K_M.gguf",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(data.Paths.SettingsFile))!.AsObject();
        Assert.Equal("OpenCode Zen", persisted["backend"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostSettings_saves_custom_backend_with_model_and_url()
    {
        using var data = TemporaryBusinessData.Create();
        var credentials = new FakeCredentialStore();
        using var factory = CreateAspNetFactory(
            data.Paths,
            credentials: credentials);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/settings",
            new
            {
                backend_choice = "custom",
                custom_model = "my-model",
                custom_url = "http://127.0.0.1:9000/v1",
            });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", payload.GetProperty("status").GetString());
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(data.Paths.SettingsFile))!.AsObject();
        Assert.Equal("Custom OpenAI", persisted["backend"]!.GetValue<string>());
        Assert.Equal("my-model", persisted["custom_model"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:9000/v1", persisted["custom_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostSettings_custom_backend_stores_key_as_credential_and_never_in_file()
    {
        using var data = TemporaryBusinessData.Create();
        var credentials = new FakeCredentialStore();
        using var factory = CreateAspNetFactory(
            data.Paths,
            credentials: credentials);
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
    }

    [Fact]
    public async Task GetSettings_returns_the_new_backend_fields_and_custom_key_presence()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"custom\",\"custom_model\":\"my-model\",\"custom_url\":\"http://localhost:9000/v1\"}");
        var credentials = new FakeCredentialStore();
        credentials.Seed("Ru", "custom_api_key", "private-custom-key");
        using var factory = CreateAspNetFactory(data.Paths, credentials: credentials);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/settings");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("custom", payload.GetProperty("backend_choice").GetString());
        Assert.Equal("my-model", payload.GetProperty("custom_model").GetString());
        Assert.Equal("http://localhost:9000/v1", payload.GetProperty("custom_url").GetString());
        Assert.True(payload.GetProperty("has_custom_api_key").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("custom_api_key").GetString());
    }

    [Fact]
    public async Task PostSettings_without_a_url_preserves_the_hand_edited_backend_url()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"custom\",\"custom_model\":\"my-model\",\"custom_url\":\"http://10.0.0.5:9000/v1\"}");
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/settings",
            new
            {
                backend_choice = "custom",
                custom_model = "my-model",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(data.Paths.SettingsFile))!.AsObject();
        Assert.Equal("http://10.0.0.5:9000/v1", persisted["custom_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostSettings_selected_model_saves_one_atomic_candidate()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"opencode\",\"opencode_model\":\"opencode/free\",\"kilo_model\":\"old-kilo\"}");
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync("/settings", new
        {
            backend_choice = "kilo",
            selected_model = "kilo-model",
        });
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", payload!["status"]!.GetValue<string>());
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(data.Paths.SettingsFile))!.AsObject();
        Assert.Equal("Kilo Gateway", persisted["backend"]!.GetValue<string>());
        Assert.Equal("kilo-model", persisted["kilo_model"]!.GetValue<string>());
        Assert.Equal("opencode/free", persisted["opencode_model"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("opencode", "OpenCode Zen", "opencode_model")]
    [InlineData("kilo", "Kilo Gateway", "kilo_model")]
    [InlineData("openai", "OpenAI", "openai_model")]
    [InlineData("anthropic", "Anthropic", "anthropic_model")]
    [InlineData("custom", "Custom OpenAI", "custom_model")]
    public async Task PostSettings_selected_model_table_covers_every_alias(
        string alias,
        string expectedBackend,
        string expectedModelField)
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"opencode\",\"opencode_model\":\"opencode/free\",\"kilo_model\":\"old-kilo\"}");
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync("/settings", new
        {
            backend_choice = alias,
            selected_model = "grouped-model",
        });
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", payload!["status"]!.GetValue<string>());
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(data.Paths.SettingsFile))!.AsObject();
        Assert.Equal(expectedBackend, persisted["backend"]!.GetValue<string>());
        Assert.Equal("grouped-model", persisted[expectedModelField]!.GetValue<string>());
        var unchangedField = expectedModelField == "opencode_model" ? "kilo_model" : "opencode_model";
        var unchangedValue = expectedModelField == "opencode_model" ? "old-kilo" : "opencode/free";
        Assert.Equal(unchangedValue, persisted[unchangedField]!.GetValue<string>());
    }

    public static TheoryData<string> InvalidModelSelectionPayloads => new()
    {
        """{"backend_choice":"unknown-provider","selected_model":"grouped-model"}""",
        """{"backend_choice":"kilo","selected_model":""}""",
        """{"backend_choice":"kilo","selected_model":"   "}""",
        """{"selected_model":"kilo-model"}""",
        """{"backend_choice":"kilo","selected_model":"grouped-model","voice_speed":1.5}""",
        """{"backend_choice":"kilo","selected_model":"grouped-model","kilo_model":"other-model"}""",
        """{"backend_choice":"kilo","selected_model":"grouped-model","model":"legacy-model"}""",
        """{"backend_choice":"kilo","selected_model":"grouped-model","kilo_api_key":"private-key"}""",
        """{"backend_choice":"kilo","selected_model":"grouped-model","opencode_api_key":"private-key"}""",
        """{"backend_choice":"kilo","selected_model":"grouped-model","unknown_provider_url":"http://127.0.0.1:9999/v1"}""",
        """{"backend_choice":"kilo","selected_model":"grouped-model","reasoning_effort":"high","reasoning_model":"grouped-model","reasoning_identity":"kilo"}""",
    };

    [Theory]
    [MemberData(nameof(InvalidModelSelectionPayloads))]
    public async Task PostSettings_invalid_selected_model_payloads_fail_closed(string json)
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"opencode\",\"opencode_model\":\"opencode/free\",\"kilo_model\":\"old-kilo\"}");
        var originalSettings = await File.ReadAllTextAsync(data.Paths.SettingsFile);
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/settings", content);
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal("error", payload!["status"]!.GetValue<string>());
        Assert.Equal(originalSettings, await File.ReadAllTextAsync(data.Paths.SettingsFile));
    }

    [Fact]
    public async Task PostSettings_selected_model_longer_than_256_characters_fails_closed()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"opencode\",\"opencode_model\":\"opencode/free\"}");
        var originalSettings = await File.ReadAllTextAsync(data.Paths.SettingsFile);
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        var json = "{\"backend_choice\":\"kilo\",\"selected_model\":\"" + new string('a', 257) + "\"}";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/settings", content);
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal("error", payload!["status"]!.GetValue<string>());
        Assert.Equal(originalSettings, await File.ReadAllTextAsync(data.Paths.SettingsFile));
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
    public async Task Missing_session_delete_returns_a_stable_error()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.DeleteAsync("/sessions/missing-session");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("error", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Session_fork_and_rewind_endpoints_work_correctly()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var createdResponse = await client.PostAsync("/sessions/new", null);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = created.GetProperty("session_id").GetString()!;

        using var forkResponse = await client.PostAsJsonAsync($"/sessions/{sessionId}/fork", new { up_to_message_index = 0, title = "Nhánh A" });
        var forked = await forkResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", forked.GetProperty("status").GetString());
        var forkedId = forked.GetProperty("session").GetProperty("id").GetString();
        Assert.NotEqual(sessionId, forkedId);
        Assert.Equal("Nhánh A", forked.GetProperty("session").GetProperty("title").GetString());
        Assert.Equal(sessionId, forked.GetProperty("session").GetProperty("parent_session_id").GetString());

        using var rewindResponse = await client.PostAsJsonAsync($"/sessions/{sessionId}/rewind", new { to_message_index = 0 });
        var rewound = await rewindResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", rewound.GetProperty("status").GetString());
        Assert.Equal(sessionId, rewound.GetProperty("session").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Activity_routes_preserve_existing_response_shapes()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.ActivityLogFile,
            "{\"ts\":1,\"iso\":\"1970-01-01T00:00:01+00:00\",\"type\":\"synthetic\",\"session_id\":null,\"data\":{\"count\":1}}\n");
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        var activity = await client.GetFromJsonAsync<JsonElement>("/activity-log?limit=200");
        using var cleared = await client.DeleteAsync("/activity-log");

        Assert.Equal("ok", activity.GetProperty("status").GetString());
        Assert.Equal("synthetic", activity.GetProperty("events")[0].GetProperty("type").GetString());
        Assert.Equal("ok", (await cleared.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString());
        Assert.False(File.Exists(data.Paths.ActivityLogFile));
    }

    private static BackendWebApplicationFactory CreateAspNetFactory(
        DataPaths paths,
        ICredentialStore? credentials = null) =>
        new(
            dataPaths: paths,
            credentialStore: credentials ?? new FakeCredentialStore(),
            diagnosticEmitter: new DiscardingDiagnosticEmitter());

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
                new DataPaths(Path.Combine(root, "settings.json"),
                    Path.Combine(root, "sessions"),
                    Path.Combine(root, "activity.jsonl")));
        }

        public void Dispose()
        {
            using (var pool = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Paths.AgentHarnessDatabase,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
            }.ToString()))
            {
                SqliteConnection.ClearPool(pool);
            }

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
