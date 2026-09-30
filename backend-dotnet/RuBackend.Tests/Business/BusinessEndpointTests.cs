using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Conversation;
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
            "{\"backend\":\"openai\",\"openai_model\":\"gpt-4o\",\"max_tokens\":128,\"allow_web_search\":false}");
        var credentials = new FakeCredentialStore();
        credentials.Seed("Ru", "openai_api_key", "private-openai-key");
        using var factory = CreateAspNetFactory(data.Paths, credentials: credentials);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/settings");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", payload.GetProperty("status").GetString());
        Assert.Equal("OpenAI", payload.GetProperty("backend").GetString());
        Assert.Equal("openai", payload.GetProperty("backend_choice").GetString());
        Assert.DoesNotContain("max_tokens", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("max_new_tokens", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("temperature", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("top_k", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_web_search", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_online_search", raw, StringComparison.Ordinal);
        Assert.True(payload.GetProperty("has_openai_api_key").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("openai_api_key").GetString());
        Assert.DoesNotContain("private-openai-key", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostSettingsProviderRemove_deletes_only_the_named_provider_credential()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"openai\",\"openai_model\":\"gpt-4o\"}");
        var credentials = new FakeCredentialStore();
        credentials.Seed("Ru", "openai_api_key", "private-openai-key");
        credentials.Seed("Ru", "custom_api_key", "private-custom-key");
        using var factory = CreateAspNetFactory(data.Paths, credentials: credentials);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/settings/provider/remove",
            new { backend_choice = "openai" });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", payload.GetProperty("status").GetString());

        using var settings = await client.GetAsync("/settings");
        var raw = await settings.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(raw)!;
        Assert.False(body["has_openai_api_key"]!.GetValue<bool>());
        Assert.True(body["has_custom_api_key"]!.GetValue<bool>());
        Assert.DoesNotContain("private-openai-key", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("private-custom-key", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostSettingsProviderRemove_is_idempotent_and_rejects_unknown_alias()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"openai\",\"openai_model\":\"gpt-4o\"}");
        var credentials = new FakeCredentialStore();
        credentials.Seed("Ru", "openai_api_key", "private-openai-key");
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
                new { backend_choice = "openai" });
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
                backend_choice = "openai",
                openai_model = "gpt-4o",
                openai_api_key = "new-private-key",
                max_new_tokens = 192,
                allow_online_search = false,
            });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", payload.GetProperty("status").GetString());
        Assert.Equal(
            "new-private-key",
            await credentials.GetAsync("Ru", "openai_api_key", CancellationToken.None));
        var persisted = await File.ReadAllTextAsync(data.Paths.SettingsFile);
        Assert.Contains("gpt-4o", persisted, StringComparison.Ordinal);
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
    public async Task PostSettings_stores_secret_byte_exact_without_trimming()
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
                backend_choice = "openai",
                openai_model = "gpt-4o",
                openai_api_key = "  padded-private-key  ",
                max_new_tokens = 192,
                allow_online_search = false,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "  padded-private-key  ",
            await credentials.GetAsync("Ru", "openai_api_key", CancellationToken.None));
    }

    [Fact]
    public async Task PostSettings_with_gguf_payload_migrates_to_models_dev()
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
        Assert.Equal("models.dev:opencode", persisted["backend"]!.GetValue<string>());
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
    public async Task PostSettings_persists_cloudflare_account_id_as_plain_text()
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
                backend_choice = "models.dev:cloudflare-workers-ai",
                cloudflare_account_id = "abc123",
            });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", payload.GetProperty("status").GetString());
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(data.Paths.SettingsFile))!.AsObject();
        Assert.Equal("abc123", persisted["cloudflare_account_id"]!.GetValue<string>());

        var settings = JsonNode.Parse(await client.GetStringAsync("/settings"))!;
        Assert.Equal("abc123", settings["cloudflare_account_id"]!.GetValue<string>());
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
            "{\"backend\":\"openai\",\"openai_model\":\"old-openai\",\"anthropic_model\":\"old-anthropic\"}");
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync("/settings", new
        {
            backend_choice = "openai",
            selected_model = "gpt-4o",
        });
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", payload!["status"]!.GetValue<string>());
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(data.Paths.SettingsFile))!.AsObject();
        Assert.Equal("OpenAI", persisted["backend"]!.GetValue<string>());
        Assert.Equal("gpt-4o", persisted["openai_model"]!.GetValue<string>());
        Assert.Equal("old-anthropic", persisted["anthropic_model"]!.GetValue<string>());
    }

    [Theory]
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
            "{\"backend\":\"openai\",\"openai_model\":\"old-openai\",\"anthropic_model\":\"old-anthropic\"}");
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
        if (expectedModelField != "openai_model")
        {
            Assert.Equal("old-openai", persisted["openai_model"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task PostSettings_generic_backend_stores_model_in_shared_field()
    {
        using var data = TemporaryBusinessData.Create();
        await File.WriteAllTextAsync(
            data.Paths.SettingsFile,
            "{\"backend\":\"openai\",\"openai_model\":\"openai/old\"}");
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync("/settings", new
        {
            backend_choice = "models.dev:openrouter",
            selected_model = "generic-model",
        });
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("success", payload!["status"]!.GetValue<string>());
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(data.Paths.SettingsFile))!.AsObject();
        Assert.Equal("models.dev:openrouter", persisted["backend"]!.GetValue<string>());
        Assert.Equal("generic-model", persisted["model"]!.GetValue<string>());
        Assert.Equal("openai/old", persisted["openai_model"]!.GetValue<string>());
    }

    public static TheoryData<string> InvalidModelSelectionPayloads => new()
    {
        """{"backend_choice":"unknown-provider","selected_model":"grouped-model"}""",
        """{"backend_choice":"models.dev:bad/id","selected_model":"grouped-model"}""",
        """{"backend_choice":"openai","selected_model":""}""",
        """{"backend_choice":"openai","selected_model":"   "}""",
        """{"selected_model":"openai-model"}""",
        """{"backend_choice":"openai","selected_model":"grouped-model","voice_speed":1.5}""",
        """{"backend_choice":"openai","selected_model":"grouped-model","openai_model":"other-model"}""",
        """{"backend_choice":"openai","selected_model":"grouped-model","model":"legacy-model"}""",
        """{"backend_choice":"openai","selected_model":"grouped-model","openai_api_key":"private-key"}""",
        """{"backend_choice":"openai","selected_model":"grouped-model","unknown_provider_url":"http://127.0.0.1:9999/v1"}""",
        """{"backend_choice":"openai","selected_model":"grouped-model","reasoning_effort":"high","reasoning_model":"grouped-model","reasoning_identity":"openai"}""",
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
            "{\"backend\":\"openai\",\"openai_model\":\"openai/old\"}");
        var originalSettings = await File.ReadAllTextAsync(data.Paths.SettingsFile);
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        var json = "{\"backend_choice\":\"openai\",\"selected_model\":\"" + new string('a', 257) + "\"}";
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
    public async Task Session_rewind_endpoint_works_correctly()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var createdResponse = await client.PostAsync("/sessions/new", null);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = created.GetProperty("session_id").GetString()!;

        using var rewindResponse = await client.PostAsJsonAsync($"/sessions/{sessionId}/rewind", new { to_message_index = 0 });
        var rewound = await rewindResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", rewound.GetProperty("status").GetString());
        Assert.Equal(sessionId, rewound.GetProperty("session").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Session_list_carries_workspace_of_latest_run()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var createdResponse = await client.PostAsync("/sessions/new", null);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = created.GetProperty("session_id").GetString()!;

        var workspacePath = Path.Combine(Path.GetTempPath(), "ru-ws-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);
        try
        {
            using var runResponse = await client.PostAsJsonAsync("/agent/v1/runs", new
            {
                session_id = sessionId,
                workspace_path = workspacePath,
                prompt = "do it",
                preset = "standard",
                idempotency_key = Guid.NewGuid().ToString("N"),
            });
            Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);

            var listed = await client.GetFromJsonAsync<JsonElement>("/sessions");
            JsonElement? entry = null;
            foreach (var element in listed.GetProperty("sessions").EnumerateArray())
            {
                if (element.GetProperty("id").GetString() == sessionId)
                {
                    entry = element;
                }
            }

            Assert.NotNull(entry);
            Assert.Equal(Path.GetFileName(workspacePath), entry.Value.GetProperty("workspace_label").GetString());
            Assert.Equal(workspacePath, entry.Value.GetProperty("workspace_path").GetString());
            Assert.False(string.IsNullOrEmpty(entry.Value.GetProperty("workspace_id").GetString()));
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task Session_list_leaves_workspace_null_without_runs()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var createdResponse = await client.PostAsync("/sessions/new", null);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = created.GetProperty("session_id").GetString()!;

        var listed = await client.GetFromJsonAsync<JsonElement>("/sessions");
        JsonElement? entry = null;
        foreach (var element in listed.GetProperty("sessions").EnumerateArray())
        {
            if (element.GetProperty("id").GetString() == sessionId)
            {
                entry = element;
            }
        }

        Assert.NotNull(entry);
        Assert.Equal(JsonValueKind.Null, entry.Value.GetProperty("workspace_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.Value.GetProperty("workspace_label").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.Value.GetProperty("workspace_path").ValueKind);
    }

    [Fact]
    public async Task Session_list_projects_legacy_branch_metadata()
    {
        using var data = TemporaryBusinessData.Create();
        var sessionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(data.Paths.SessionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(data.Paths.SessionDirectory, sessionId + ".json"),
            JsonSerializer.Serialize(new
            {
                id = sessionId,
                title = "legacy branch",
                created_at = now,
                updated_at = now,
                messages = Array.Empty<object>(),
                parent_session_id = "parent-session",
                forked_from_message_id = "msg-2",
            }));
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        var listed = await client.GetFromJsonAsync<JsonElement>("/sessions");
        JsonElement? entry = null;
        foreach (var element in listed.GetProperty("sessions").EnumerateArray())
        {
            if (element.GetProperty("id").GetString() == sessionId)
            {
                entry = element;
            }
        }

        Assert.NotNull(entry);
        Assert.Equal("parent-session", entry.Value.GetProperty("parent_session_id").GetString());
        Assert.Equal("msg-2", entry.Value.GetProperty("forked_from_message_id").GetString());
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

    /// <summary>
    /// A conversation that leaves in one agent's format comes back as the same
    /// conversation. That round trip is the whole promise of the interchange routes.
    /// </summary>
    [Fact]
    public async Task Session_interchange_round_trips_a_conversation_through_another_agent()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        using var createdResponse = await client.PostAsync("/sessions/new", null);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = created.GetProperty("session_id").GetString()!;
        var store = factory.Services.GetRequiredService<ISessionStore>();
        Assert.True(await store.AppendAsync(
            sessionId,
            [
                new SessionMessage("user", "Bring me across"),
                new SessionMessage("ai", "I am here"),
            ],
            CancellationToken.None));

        var exported = await client.GetFromJsonAsync<JsonElement>(
            $"/sessions/{sessionId}/export?harness=claude-code");
        Assert.Equal("ok", exported.GetProperty("status").GetString());

        var claudeFile = exported.GetProperty("files")[0];
        var claudeContent = claudeFile.GetProperty("content").GetString()!;
        Assert.EndsWith(".jsonl", claudeFile.GetProperty("name").GetString()!, StringComparison.Ordinal);
        Assert.Contains("Bring me across", claudeContent, StringComparison.Ordinal);

        // Back in as the format the file actually is, then out again as the other agent:
        // two hops, and the words are still there at the end of them.
        using var importedResponse = await client.PostAsJsonAsync(
            "/sessions/import",
            new { harness = "claude-code", content = claudeContent });
        var imported = await importedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", imported.GetProperty("status").GetString());
        Assert.Equal(2, imported.GetProperty("session").GetProperty("messages").GetInt32());

        var reExported = await client.GetFromJsonAsync<JsonElement>(
            $"/sessions/{imported.GetProperty("session").GetProperty("id").GetString()}/export?harness=codex");
        var reExportedContent = reExported.GetProperty("files")[0].GetProperty("content").GetString()!;

        Assert.Contains("Bring me across", reExportedContent, StringComparison.Ordinal);
        Assert.True(
            reExportedContent.Contains("I am here", StringComparison.Ordinal),
            $"The assistant turn was lost on the way back: {reExportedContent}");
    }

    [Fact]
    public async Task Session_import_counts_the_entries_it_could_not_store()
    {
        // Ru's session log holds turns, not steps: reasoning and tool traffic belong to
        // runs that really executed something. They are counted back to the caller rather
        // than written in as messages no model produced.
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        const string Rollout = """
            {"timestamp":"2026-09-07T15:14:34.810Z","ordinal":0,"type":"session_meta","payload":{"session_id":"01a07c6e-aff5-7fa3-b7f6-9e7cd8096ae4","id":"01a07c6e-aff5-7fa3-b7f6-9e7cd8096ae4","timestamp":"2026-09-07T15:13:44.040Z","cwd":"D:\\LLM","thread_source":"user"}}
            {"timestamp":"2026-09-07T15:14:35.000Z","ordinal":1,"type":"response_item","payload":{"type":"message","id":"msg_1","role":"user","content":[{"type":"input_text","text":"Fix the widget"}]}}
            {"timestamp":"2026-09-07T15:14:36.000Z","ordinal":2,"type":"response_item","payload":{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"Planning the edit"}],"content":null}}
            {"timestamp":"2026-09-07T15:14:37.000Z","ordinal":3,"type":"response_item","payload":{"type":"function_call","id":"fc_1","name":"exec","arguments":"{}","call_id":"call_1"}}
            {"timestamp":"2026-09-07T15:14:38.000Z","ordinal":4,"type":"response_item","payload":{"type":"function_call_output","id":"fco_1","call_id":"call_1","output":"Script completed"}}
            {"timestamp":"2026-09-07T15:14:39.000Z","ordinal":5,"type":"response_item","payload":{"type":"message","id":"msg_2","role":"assistant","content":[{"type":"output_text","text":"Fixed the widget"}]}}
            """;

        using var response = await client.PostAsJsonAsync(
            "/sessions/import",
            new { harness = "codex", content = Rollout });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ok", payload.GetProperty("status").GetString());
        Assert.Equal(2, payload.GetProperty("session").GetProperty("messages").GetInt32());
        Assert.Equal(3, payload.GetProperty("dropped_entries").GetInt32());
    }

    [Fact]
    public async Task Session_import_rejects_content_above_the_shared_cap_with_a_413()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        // Just over the cap the CLI and GUI measure before reading. The body itself is
        // below the transport bound, so the endpoint's own content check must be the one
        // that answers.
        var oversized = new string('a', RuAgent.SessionInterchange.SessionInterchangeLimits.MaxImportContentBytes + 1);
        using var response = await client.PostAsJsonAsync(
            "/sessions/import",
            new { harness = "claude-code", content = oversized });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            "import_too_large",
            payload.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_interchange_names_the_formats_it_knows_instead_of_failing_silently()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        var exported = await client.GetFromJsonAsync<JsonElement>("/sessions/anything/export?harness=cursor");
        Assert.Equal("error", exported.GetProperty("status").GetString());
        Assert.Contains("claude-code", exported.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Contains("codex", exported.GetProperty("message").GetString()!, StringComparison.Ordinal);

        using var imported = await client.PostAsJsonAsync(
            "/sessions/import",
            new { harness = "codex", content = "{\"hello\":\"world\"}" });
        var payload = await imported.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("error", payload.GetProperty("status").GetString());
        Assert.Contains("Codex", payload.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A partner whose store Ru cannot produce is refused before a file is written, and the
    /// refusal names what is missing rather than calling the format unsupported.
    /// </summary>
    [Fact]
    public async Task Session_export_refuses_a_format_it_cannot_write()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        var exported = await client.GetFromJsonAsync<JsonElement>("/sessions/anything/export?harness=antigravity");

        Assert.Equal("error", exported.GetProperty("status").GetString());
        Assert.Contains("protobuf", exported.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_export_refuses_a_session_that_does_not_exist()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();

        var exported = await client.GetFromJsonAsync<JsonElement>("/sessions/missing/export?harness=codex");

        Assert.Equal("error", exported.GetProperty("status").GetString());
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
            // All pools, not one string: pools are keyed by the full connection
            // string, and the backend opens this file through several option sets.
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
