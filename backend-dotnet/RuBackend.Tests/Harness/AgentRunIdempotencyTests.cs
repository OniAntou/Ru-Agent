using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Client;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol.Harness;
using RuBackend.Business.Settings;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class AgentRunIdempotencyTests
{
    [Fact]
    public async Task Shared_client_accepts_host_create_projection_and_gets_enriched_run_metadata()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var http = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(http);
        var descriptor = new RuntimeDescriptor(
            RuntimeDescriptor.CurrentProtocolVersion,
            http.BaseAddress!.ToString(),
            "test-secret",
            Environment.ProcessId,
            "0",
            "test-instance",
            DateTimeOffset.UtcNow.ToString("O"));
        using var client = new AgentApiClient(new PublicHostSession(http.BaseAddress, "test-secret", descriptor), http);
        var preset = await client.SetSessionPresetAsync(sessionId, "standard", 0, interactive: true);

        var created = await client.CreateRunAsync(new CreateAgentRunRequest(
            sessionId,
            tmp.Path,
            "created through the shared API client",
            "standard",
            preset.GetProperty("version").GetInt64(),
            "create-client-metadata-" + Guid.NewGuid().ToString("N")));
        var loaded = await client.GetRunAsync(created.RunId);

        Assert.Equal(created.RunId, loaded.RunId);
        Assert.Null(created.ProviderBackend);
        Assert.Null(created.ProviderModel);
        Assert.Null(loaded.ProviderProtocol);
    }

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
    public async Task Different_sessions_share_one_workspace_without_configuration()
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
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        var j = await r2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(blockingRunId, j.GetProperty("run_id").GetString());
        Assert.Equal(created.GetProperty("workspace_id").GetString(), j.GetProperty("workspace_id").GetString());
        Assert.Equal("waiting_workspace_trust", j.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Parallel_workspaces_cannot_be_disabled_and_keep_each_session_single_run()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var firstSession = await CreateSessionAsync(client);
        var secondSession = await CreateSessionAsync(client);
        object Request(string session) => new
        {
            session_id = session, workspace_path = tmp.Path, prompt = "independent task",
            preset = "standard", idempotency_key = Guid.NewGuid().ToString("N"),
        };
        using var first = await client.PostAsJsonAsync("/agent/v1/runs", Request(firstSession));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstRun = await first.Content.ReadFromJsonAsync<JsonElement>();
        string workspaceId = firstRun.GetProperty("workspace_id").GetString()!;
        string policyUrl = $"/agent/v1/workspaces/{workspaceId}/concurrency";
        var policy = await client.GetFromJsonAsync<WorkspaceConcurrencyResponse>(policyUrl);
        Assert.Equal("shared", policy!.Policy.Mode);
        Assert.Equal(32, policy.Policy.MaxConcurrentRuns);
        using var disable = await client.PutAsJsonAsync(policyUrl, new WorkspaceConcurrencyUpdateRequest("exclusive", 1));
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
        using var singleSlot = await client.PutAsJsonAsync(policyUrl, new WorkspaceConcurrencyUpdateRequest("shared", 1));
        Assert.Equal(HttpStatusCode.Conflict, singleSlot.StatusCode);
        using var second = await client.PostAsJsonAsync("/agent/v1/runs", Request(secondSession));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        using var duplicateSession = await client.PostAsJsonAsync("/agent/v1/runs", Request(firstSession));
        Assert.Equal(HttpStatusCode.Conflict, duplicateSession.StatusCode);
        var existing = await client.GetFromJsonAsync<JsonElement>($"/agent/v1/runs/{firstRun.GetProperty("run_id").GetString()}");
        Assert.Equal("waiting_workspace_trust", existing.GetProperty("status").GetString());
        Assert.Equal("standard", existing.GetProperty("preset").GetString());
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
    public async Task Pinned_preset_that_no_longer_matches_the_stored_selection_is_rejected_before_creating_a_run()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var session = await CreateSessionAsync(client);

        // The first pinned create stores standard at version 1.
        var firstBody = new
        {
            session_id = session,
            workspace_path = tmp.Path,
            prompt = "preset pin first",
            preset = "standard",
            idempotency_key = "pin-1-" + Guid.NewGuid().ToString("N"),
            preset_selection_version = 1,
        };
        using var first = await client.PostAsJsonAsync("/agent/v1/runs", firstBody);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        await client.PostAsync($"/agent/v1/runs/{firstJson.GetProperty("run_id").GetString()}/cancel", null);

        // A stale client still pins version 1 but asks for plan. No run may be
        // created: a live run carrying a preset the caller cannot bind would be
        // impossible for that caller to cancel.
        var staleBody = new
        {
            session_id = session,
            workspace_path = tmp.Path,
            prompt = "preset pin stale",
            preset = "plan",
            idempotency_key = "pin-2-" + Guid.NewGuid().ToString("N"),
            preset_selection_version = 1,
        };
        using var stale = await client.PostAsJsonAsync("/agent/v1/runs", staleBody);
        Assert.Equal((HttpStatusCode)409, stale.StatusCode);
        var staleJson = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("preset_mismatch", staleJson.GetProperty("error_code").GetString());

        // The rejection did not consume the session or its pinned version.
        var retryBody = new
        {
            session_id = session,
            workspace_path = tmp.Path,
            prompt = "preset pin retry",
            preset = "standard",
            idempotency_key = "pin-3-" + Guid.NewGuid().ToString("N"),
            preset_selection_version = 1,
        };
        using var retry = await client.PostAsJsonAsync("/agent/v1/runs", retryBody);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var retryJson = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("standard", retryJson.GetProperty("preset").GetString());
        await client.PostAsync($"/agent/v1/runs/{retryJson.GetProperty("run_id").GetString()}/cancel", null);
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
