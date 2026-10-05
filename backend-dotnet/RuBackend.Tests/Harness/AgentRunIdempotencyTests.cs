using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Settings;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentRunIdempotencyTests
{
    [Fact]
    public async Task Get_run_returns_durable_provider_pin_after_settings_change()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        using var created = await client.PostAsJsonAsync("/agent/v1/runs", new
        {
            session_id = sessionId,
            workspace_path = tmp.Path,
            prompt = "provider metadata",
            preset = "standard",
            idempotency_key = "provider-metadata-" + Guid.NewGuid().ToString("N"),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();
        var runId = createdJson.GetProperty("run_id").GetString()!;

        var selections = factory.Services.GetRequiredService<SqliteAgentRunProviderSelectionStore>();
        await selections.PinAsync(new RunId(runId), new AgentRunProviderSelection(
            "Custom OpenAI", "custom", "chat_completions", "https://provider-a.invalid/v1",
            "model-a", null, "{}", null, "Model A", "text_selection", 32_000,
            "provider_catalog", "catalog-test-v1", 4096), default);
        var settings = factory.Services.GetRequiredService<ISettingsStore>();
        var current = await settings.ReadAsync(default);
        await settings.SaveAsync(current with
        {
            Backend = "Custom OpenAI",
            CustomModel = "model-b",
            CustomUrl = "https://provider-b.invalid/v1",
        }, default);

        using var response = await client.GetAsync($"/agent/v1/runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Custom OpenAI", body.GetProperty("provider_backend").GetString());
        Assert.Equal("model-a", body.GetProperty("provider_model").GetString());
        Assert.Equal("chat_completions", body.GetProperty("provider_protocol").GetString());
        Assert.Equal("Model A", body.GetProperty("provider_model_label").GetString());
        Assert.Equal("text_selection", body.GetProperty("provider_calling_mode").GetString());
        Assert.Equal(32_000, body.GetProperty("context_window_tokens").GetInt64());
        Assert.Equal("provider_catalog", body.GetProperty("context_window_source").GetString());
        Assert.Equal("catalog-test-v1", body.GetProperty("context_window_version").GetString());
        Assert.Equal(4096, body.GetProperty("output_token_reserve").GetInt32());
        Assert.Equal("available", body.GetProperty("work_state_status").GetString());
        Assert.Contains("provider metadata", body.GetProperty("work_state_preview").GetString());
        Assert.False(body.GetProperty("work_state_preview_omitted").GetBoolean());
        Assert.Equal(runId, body.GetProperty("work_state_source_run_id").GetString());
        Assert.StartsWith("evt_", body.GetProperty("work_state_source_event_id").GetString());
        Assert.True(DateTimeOffset.TryParse(body.GetProperty("work_state_updated_at").GetString(), out _));
    }

    [Fact]
    public async Task Get_run_leaves_provider_metadata_null_when_run_has_no_pin()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        using var created = await client.PostAsJsonAsync("/agent/v1/runs", new
        {
            session_id = sessionId,
            workspace_path = tmp.Path,
            prompt = "unselected provider metadata",
            preset = "standard",
            idempotency_key = "provider-metadata-empty-" + Guid.NewGuid().ToString("N"),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();
        var runId = createdJson.GetProperty("run_id").GetString()!;

        using var response = await client.GetAsync($"/agent/v1/runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertNullOrMissing(body, "provider_backend");
        AssertNullOrMissing(body, "provider_model");
        AssertNullOrMissing(body, "provider_protocol");
        Assert.Equal("available", body.GetProperty("work_state_status").GetString());
    }

    private static void AssertNullOrMissing(JsonElement body, string propertyName)
    {
        Assert.True(!body.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Create_returns_201_and_idempotent_replay_returns_200_same_run()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);

        var body = new
        {
            session_id = sessionId,
            workspace_path = tmp.Path,
            prompt = "hello tracer " + Guid.NewGuid().ToString("N"),
            preset = "standard",
            idempotency_key = "key-" + Guid.NewGuid().ToString("N"),
        };

        using var resp1 = await client.PostAsJsonAsync("/agent/v1/runs", body);
        Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);
        var json1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        var runId1 = json1.GetProperty("run_id").GetString()!;

        using var resp2 = await client.PostAsJsonAsync("/agent/v1/runs", body);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var json2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        var runId2 = json2.GetProperty("run_id").GetString()!;
        Assert.Equal(runId1, runId2);
    }

    [Fact]
    public async Task Same_key_different_normalized_request_returns_idempotency_conflict()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var key = "conflict-key-" + Guid.NewGuid().ToString("N");

        var body1 = new
        {
            session_id = sessionId,
            workspace_path = tmp.Path,
            prompt = "prompt one",
            preset = "standard",
            idempotency_key = key,
        };

        using var r1 = await client.PostAsJsonAsync("/agent/v1/runs", body1);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        var body2 = new
        {
            session_id = sessionId,
            workspace_path = tmp.Path,
            prompt = "prompt two different",
            preset = "standard",
            idempotency_key = key,
        };

        using var r2 = await client.PostAsJsonAsync("/agent/v1/runs", body2);
        Assert.Equal((HttpStatusCode)409, r2.StatusCode);
        var j = await r2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency_conflict", j.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Active_workspace_conflict_returns_workspace_busy()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var session1 = await CreateSessionAsync(client);
        var session2 = await CreateSessionAsync(client);

        var body1 = new
        {
            session_id = session1,
            workspace_path = tmp.Path,
            prompt = "first run",
            preset = "standard",
            idempotency_key = "wk-busy-1-" + Guid.NewGuid().ToString("N"),
        };

        using var r1 = await client.PostAsJsonAsync("/agent/v1/runs", body1);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        var created = await r1.Content.ReadFromJsonAsync<JsonElement>();
        var blockingRunId = created.GetProperty("run_id").GetString();

        var body2 = new
        {
            session_id = session2,
            workspace_path = tmp.Path,
            prompt = "second run same workspace",
            preset = "standard",
            idempotency_key = "wk-busy-2-" + Guid.NewGuid().ToString("N"),
        };

        using var r2 = await client.PostAsJsonAsync("/agent/v1/runs", body2);
        Assert.Equal((HttpStatusCode)409, r2.StatusCode);
        var j = await r2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workspace_busy", j.GetProperty("error_code").GetString());
        // The client needs the owning run to point the operator at the exact run to
        // cancel or attach to instead of only reporting a bare busy code.
        Assert.Equal(blockingRunId, j.GetProperty("blocking_run_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(j.GetProperty("blocking_run_status").GetString()));
    }

    [Fact]
    public async Task Preset_version_mismatch_returns_409()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var session = await CreateSessionAsync(client);

        var body1 = new
        {
            session_id = session,
            workspace_path = tmp.Path,
            prompt = "preset test first",
            preset = "standard",
            idempotency_key = "preset-1-" + Guid.NewGuid().ToString("N"),
            preset_selection_version = 1,
        };

        using var r1 = await client.PostAsJsonAsync("/agent/v1/runs", body1);
        var firstBody = await r1.Content.ReadAsStringAsync();
        Assert.True(
            r1.StatusCode == HttpStatusCode.Created,
            $"Fresh workspace run should be created before the version mismatch request. "
                + $"Actual status: {(int)r1.StatusCode}; body: {firstBody}");
        var runId = JsonDocument.Parse(firstBody).RootElement.GetProperty("run_id").GetString()!;
        await client.PostAsync($"/agent/v1/runs/{runId}/cancel", null);

        // Second attempt with mismatched version
        var body2 = new
        {
            session_id = session,
            workspace_path = tmp.Path,
            prompt = "preset test second",
            preset = "standard",
            idempotency_key = "preset-2-" + Guid.NewGuid().ToString("N"),
            preset_selection_version = 2,
        };

        using var r2 = await client.PostAsJsonAsync("/agent/v1/runs", body2);
        Assert.Equal((HttpStatusCode)409, r2.StatusCode);
        var j = await r2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("preset_mismatch", j.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Legacy_full_preset_without_version_creates_standard_run()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var session = await CreateSessionAsync(client);

        var body = new
        {
            session_id = session,
            workspace_path = tmp.Path,
            prompt = "full without version",
            preset = "full_access",
            idempotency_key = "full-no-ver-" + Guid.NewGuid().ToString("N"),
        };

        using var resp = await client.PostAsJsonAsync("/agent/v1/runs", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var j = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("standard", j.GetProperty("preset").GetString());
    }

    [Fact]
    public async Task First_standard_without_version_creates_version_1_atomically()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var session = await CreateSessionAsync(client);

        var body = new
        {
            session_id = session,
            workspace_path = tmp.Path,
            prompt = "first standard no version",
            preset = "standard",
            idempotency_key = "first-std-no-ver-" + Guid.NewGuid().ToString("N"),
        };

        using var resp = await client.PostAsJsonAsync("/agent/v1/runs", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("standard", json.GetProperty("preset").GetString());
        Assert.Equal(1, json.GetProperty("policy_snapshot_version").GetInt64());
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Path { get; }
        public RuBackend.Business.DataPaths DataPaths { get; }
        private readonly string _root;

        private TempWorkspace(string root, string wsPath, RuBackend.Business.DataPaths dataPaths)
        {
            _root = root;
            Path = wsPath;
            DataPaths = dataPaths;
        }

        public static TempWorkspace Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ru-agent-test-" + Guid.NewGuid().ToString("N"));
            var ws = System.IO.Path.Combine(root, "ws");
            Directory.CreateDirectory(ws);
            var dataRoot = System.IO.Path.Combine(root, "data");
            Directory.CreateDirectory(dataRoot);
            var resolved = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, System.IO.Path.Combine(root, "logs"));
            return new TempWorkspace(root, ws, resolved);
        }

        public void Dispose()
        {
            try { SqliteTestPools.ClearHarnessDatabase(DataPaths.AgentHarnessDatabase); } catch { }
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
