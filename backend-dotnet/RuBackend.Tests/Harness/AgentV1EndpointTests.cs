using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Models;
using RuBackend.Business.Settings;
using RuBackend.Harness;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentV1EndpointTests
{
    private static readonly (string Method, string Path)[] ExpectedRoutes =
    {
        ("POST", "/agent/v1/runs"),
        ("GET", "/agent/v1/runs/{runId}"),
        ("GET", "/agent/v1/runs/{runId}/subagents"),
        ("GET", "/agent/v1/runs/{runId}/subagents/{agentId}"),

        ("GET", "/agent/v1/runs/{runId}/events"),
        ("GET", "/agent/v1/runs/{runId}/activities"),
        ("GET", "/agent/v1/runs/{runId}/payloads/{payloadId}"),
        ("POST", "/agent/v1/runs/{runId}/approvals/{approvalId}"),
        ("POST", "/agent/v1/runs/{runId}/inputs/{inputId}"),
        ("POST", "/agent/v1/runs/{runId}/trust"),
        ("POST", "/agent/v1/runs/{runId}/cancel"),
        ("POST", "/agent/v1/runs/{runId}/continue"),
        ("POST", "/agent/v1/runs/{runId}/compact"),
        ("POST", "/agent/v1/runs/{runId}/commands"),
        ("POST", "/agent/v1/runs/{runId}/reconcile"),
        ("GET", "/agent/v1/sessions/{sessionId}/runs"),
        ("GET", "/agent/v1/sessions/{sessionId}/preset"),
        ("GET", "/agent/v1/policies"),
        ("POST", "/agent/v1/policies"),
        ("DELETE", "/agent/v1/policies/{ruleId}"),
        ("GET", "/agent/v1/workspaces/{workspaceId}/concurrency"),
        ("PUT", "/agent/v1/workspaces/{workspaceId}/concurrency"),
        ("GET", "/agent/v1/workspaces/{workspaceId}/activity"),
        ("GET", "/agent/v1/workspaces/{workspaceId}/changes"),
        ("POST", "/agent/v1/workspaces/{workspaceId}/changes/{changeId}/restore"),
    };

    [Fact]
    public void Route_manifest_contains_agent_routes()
    {
        var contractPath = FindContractPath();
        var json = File.ReadAllText(contractPath);
        var doc = JsonDocument.Parse(json);
        var routes = doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("path").GetString()!.StartsWith("/agent/v1", StringComparison.Ordinal))
            .Select(e => (e.GetProperty("method").GetString()!, e.GetProperty("path").GetString()!))
            .ToHashSet();

        var expected = ExpectedRoutes.ToHashSet();
        Assert.Equal(expected, routes);
        Assert.Equal(46, doc.RootElement.GetArrayLength()); // 46 HTTP routes (21 non-Agent + 25 /agent/v1/*), no websocket
    }

    [Theory]
    [InlineData("POST", "/agent/v1/runs")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/events")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/activities")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/payloads/payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approvals/apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/inputs/inp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/trust")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/cancel")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/continue")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/compact")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/commands")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/reconcile")]
    [InlineData("GET", "/agent/v1/sessions/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/runs")]
    [InlineData("GET", "/agent/v1/sessions/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/preset")]
    [InlineData("GET", "/agent/v1/policies")]
    [InlineData("POST", "/agent/v1/policies")]
    [InlineData("DELETE", "/agent/v1/policies/rule_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("GET", "/agent/v1/workspaces/ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/concurrency")]
    [InlineData("PUT", "/agent/v1/workspaces/ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/concurrency")]
    [InlineData("GET", "/agent/v1/workspaces/ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/activity")]
    [InlineData("GET", "/agent/v1/workspaces/ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/changes")]
    [InlineData("POST", "/agent/v1/workspaces/ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/changes/chg_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/restore")]
    public async Task Agent_routes_reject_missing_bearer_with_401(string method, string path)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient(); // no auth
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/agent/v1/runs")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/events")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/activities")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/payloads/payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approvals/apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/inputs/inp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/trust")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/cancel")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/continue")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/compact")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/commands")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/reconcile")]
    [InlineData("GET", "/agent/v1/sessions/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/runs")]
    [InlineData("GET", "/agent/v1/sessions/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/preset")]
    [InlineData("GET", "/agent/v1/policies")]
    [InlineData("POST", "/agent/v1/policies")]
    [InlineData("DELETE", "/agent/v1/policies/rule_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("GET", "/agent/v1/workspaces/ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/concurrency")]
    [InlineData("PUT", "/agent/v1/workspaces/ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/concurrency")]
    [InlineData("GET", "/agent/v1/workspaces/ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/activity")]
    [InlineData("GET", "/agent/v1/workspaces/ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/changes")]
    public async Task Agent_routes_reject_wrong_bearer_with_401(string method, string path)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-secret");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Policy_and_approval_and_reconcile_return_phase_not_available()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        // Task 9: policies are now active – GET should return 200 (empty list), not 409
        using var getPol = await client.GetAsync("/agent/v1/policies");
        Assert.Equal(200, (int)getPol.StatusCode);
        var getJson = await getPol.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(getJson.TryGetProperty("items", out var items));
        Assert.Equal(JsonValueKind.Array, items.ValueKind);

        // POST with invalid body should return 400, not 409
        using var postPol = await client.PostAsJsonAsync("/agent/v1/policies", new { rule = new { } });
        Assert.NotEqual(409, (int)postPol.StatusCode);
        Assert.True(postPol.StatusCode == HttpStatusCode.BadRequest || postPol.StatusCode == HttpStatusCode.Forbidden);

        // DELETE with well-formed id should not be 409 (either 200/404/403)
        using var delPol = await client.DeleteAsync("/agent/v1/policies/rule_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        Assert.NotEqual(409, (int)delPol.StatusCode);

        // Approvals are wired – dummy run returns run_not_found, not phase_not_available
        using var appr = await client.PostAsJsonAsync("/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approvals/apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new { });
        Assert.NotEqual(409, (int)appr.StatusCode);
        var apprJson = await appr.Content.ReadFromJsonAsync<JsonElement>();
        if (apprJson.TryGetProperty("error_code", out var code))
        {
            Assert.NotEqual("phase_not_available", code.GetString());
        }

        using var rec = await client.PostAsJsonAsync("/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/reconcile", new { outcome = "applied" });
        // The reconcile endpoint is live since Task 8: a missing snapshot_hash fails
        // validation before any run lookup, never with a phase stub.
        Assert.Equal(400, (int)rec.StatusCode);
        var recJson = await rec.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_snapshot_hash", recJson.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Compact_route_reports_runtime_not_ready_without_a_live_coordinator()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.PostAsJsonAsync(
            "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/compact",
            new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("runtime_not_ready", json.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Create_run_accepts_valid_image_attachments_and_persists_payloads()
    {
        using var tmp = TempWorkspace.Create();
        var snapshot = CreateSettingsSnapshot("OpenAI", "gpt-4o");
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths, settingsStore: new StubSettingsStore(snapshot));
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);

        var validPngBase64 = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        using var response = await client.PostAsJsonAsync(
            "/agent/v1/runs",
            new
            {
                session_id = sessionId,
                workspace_path = tmp.Path,
                prompt = "inspect image",
                preset = "standard",
                idempotency_key = "image-valid-test-" + Guid.NewGuid().ToString("N"),
                image_attachments = new[] { validPngBase64 },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runId = json.GetProperty("run_id").GetString()!;
        Assert.False(string.IsNullOrEmpty(runId));

        using var getRun = await client.GetAsync($"/agent/v1/runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, getRun.StatusCode);

        // Verify UserImage payload persisted in SQLite and readable via payload endpoint
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tmp.DataPaths.AgentHarnessDatabase};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload_id, kind, audience FROM agent_payloads WHERE run_id = $runId AND kind = 11";
        cmd.Parameters.AddWithValue("$runId", runId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var payloadId = reader.GetString(0);
        Assert.Equal(11, reader.GetInt32(1));
        Assert.Equal(3, reader.GetInt32(2)); // AuthenticatedClient (1) | ModelContext (2) = 3

        using var getPayload = await client.GetAsync($"/agent/v1/runs/{runId}/payloads/{payloadId}");
        Assert.Equal(HttpStatusCode.OK, getPayload.StatusCode);
        var payloadJson = await getPayload.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(validPngBase64, payloadJson.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Create_run_rejects_five_images_with_image_limit_exceeded()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var fiveImages = Enumerable.Repeat("data:image/png;base64,aGVsbG8=", 5).ToArray();
        using var response = await client.PostAsJsonAsync(
            "/agent/v1/runs",
            new
            {
                session_id = "sess_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                workspace_path = Path.GetTempPath(),
                prompt = "inspect images",
                preset = "standard",
                idempotency_key = "image-limit-test",
                image_attachments = fiveImages,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("image_limit_exceeded", json.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Create_run_rejects_invalid_mime()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync(
            "/agent/v1/runs",
            new
            {
                session_id = "sess_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                workspace_path = Path.GetTempPath(),
                prompt = "inspect image",
                preset = "standard",
                idempotency_key = "image-mime-test",
                image_attachments = new[] { "data:image/gif;base64,aGVsbG8=" },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_image_mime", json.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Create_run_rejects_invalid_base64()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.PostAsJsonAsync(
            "/agent/v1/runs",
            new
            {
                session_id = "sess_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                workspace_path = Path.GetTempPath(),
                prompt = "inspect image",
                preset = "standard",
                idempotency_key = "image-b64-test",
                image_attachments = new[] { "data:image/png;base64,not valid base64 with spaces!" },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_image_base64", json.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Create_run_rejects_http_body_larger_than_the_transport_cap()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        // Above the 24MB runner cap but below the 26MB middleware bound, so the
        // endpoint itself must issue the 413.
        var oversized = "data:image/png;base64," + new string('A', 25 * 1024 * 1024);
        using var response = await client.PostAsJsonAsync(
            "/agent/v1/runs",
            new
            {
                session_id = "sess_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                workspace_path = Path.GetTempPath(),
                prompt = "inspect image",
                preset = "standard",
                idempotency_key = "image-large-http-test",
                image_attachments = new[] { oversized },
            });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("request_too_large", json.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Create_run_accepts_an_image_above_the_legacy_1mb_transport_cap()
    {
        using var tmp = TempWorkspace.Create();
        var snapshot = CreateSettingsSnapshot("OpenAI", "gpt-4o");
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths, settingsStore: new StubSettingsStore(snapshot));
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);

        // 2.5MB decoded → ~3.4M base64 characters, well above the old 1MB body
        // cap but inside the 4MB per-image and 20MB total service bounds.
        var imageBytes = new byte[2_500_000];
        Random.Shared.NextBytes(imageBytes);
        var largeBase64 = "data:image/png;base64," + Convert.ToBase64String(imageBytes);
        using var response = await client.PostAsJsonAsync(
            "/agent/v1/runs",
            new
            {
                session_id = sessionId,
                workspace_path = tmp.Path,
                prompt = "inspect large image",
                preset = "standard",
                idempotency_key = "image-large-accepted-" + Guid.NewGuid().ToString("N"),
                image_attachments = new[] { largeBase64 },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_run_service_rejects_image_too_large_per_image()
    {
        using var factory = new BackendWebApplicationFactory();
        var svc = factory.Services.GetRequiredService<AgentV1Service>();
        var largeBytes = new byte[4 * 1024 * 1024 + 4];
        var largeBase64 = "data:image/png;base64," + Convert.ToBase64String(largeBytes);
        var req = new CreateAgentRunRequest(
            "sess_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Path.GetTempPath(),
            "inspect image",
            "standard",
            1,
            "image-large-test",
            new[] { largeBase64 });

        var result = await svc.CreateRunAsync(req, CancellationToken.None);
        Assert.Equal(413, result.StatusCode);
        var err = Assert.IsType<AgentErrorResponse>(result.Body);
        Assert.Equal("image_too_large", err.ErrorCode);
    }

    [Fact]
    public async Task Create_run_service_rejects_image_chars_exceeds_5600000()
    {
        using var factory = new BackendWebApplicationFactory();
        var svc = factory.Services.GetRequiredService<AgentV1Service>();
        var longUrl = "data:image/png;base64," + new string('A', 5_600_001);
        var req = new CreateAgentRunRequest(
            "sess_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Path.GetTempPath(),
            "inspect image",
            "standard",
            1,
            "image-chars-test",
            new[] { longUrl });

        var result = await svc.CreateRunAsync(req, CancellationToken.None);
        Assert.Equal(413, result.StatusCode);
        var err = Assert.IsType<AgentErrorResponse>(result.Body);
        Assert.Equal("image_too_large", err.ErrorCode);
    }

    [Fact]
    public async Task Create_run_rejects_vision_unsupported_model()
    {
        using var tmp = TempWorkspace.Create();
        var snapshot = CreateSettingsSnapshot("OpenAI", "deepseek-chat");
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths, settingsStore: new StubSettingsStore(snapshot));
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);

        var validPngBase64 = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        using var response = await client.PostAsJsonAsync(
            "/agent/v1/runs",
            new
            {
                session_id = sessionId,
                workspace_path = tmp.Path,
                prompt = "inspect image",
                preset = "standard",
                idempotency_key = "image-vision-unsupported-test-" + Guid.NewGuid().ToString("N"),
                image_attachments = new[] { validPngBase64 },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("vision_unsupported", json.GetProperty("error_code").GetString());
    }

    [Fact]
    public void IsVisionModel_prefers_a_provider_declaration_over_the_model_name()
    {
        // A gateway model name carries no vendor token, so the name check alone reads
        // it as non-vision. A provider that publishes ["text","image"] is the
        // authority, and its declaration must win in both directions.
        Assert.True(ModelImage.IsVisionModel("dots-studio/dots-3-note-preview:free", declaredVision: true));
        Assert.False(ModelImage.IsVisionModel("gpt-4o", declaredVision: false));
    }

    [Fact]
    public void IsVisionModel_falls_back_to_the_name_when_nothing_is_declared()
    {
        // Absent is not a denial: an undeclared model keeps the weaker signal so a
        // known vision model is not newly rejected.
        Assert.True(ModelImage.IsVisionModel("gpt-4o", declaredVision: null));
        Assert.False(ModelImage.IsVisionModel("deepseek-chat", declaredVision: null));
    }

    [Fact]
    public async Task Create_run_accepts_empty_prompt_when_image_present()
    {
        using var tmp = TempWorkspace.Create();
        var snapshot = CreateSettingsSnapshot("OpenAI", "gpt-4o");
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths, settingsStore: new StubSettingsStore(snapshot));
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);

        var validPngBase64 = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        using var response = await client.PostAsJsonAsync(
            "/agent/v1/runs",
            new
            {
                session_id = sessionId,
                workspace_path = tmp.Path,
                prompt = "",
                preset = "standard",
                idempotency_key = "image-empty-prompt-test-" + Guid.NewGuid().ToString("N"),
                image_attachments = new[] { validPngBase64 },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Trust_endpoint_rejects_display_hash_mismatch_without_state_change()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var created = await CreateRunAsync(client, sessionId, tmp.Path);

        using var response = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{created.GetProperty("run_id").GetString()}/trust",
            new
            {
                trust_request_id = created.GetProperty("trust_request_id").GetString(),
                display_identity_hash = new string('0', 64),
                decision = "approve",
            });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("trust_stale", body.GetProperty("error_code").GetString());
        Assert.Equal("trust_display_hash_mismatch", body.GetProperty("message").GetString());
        await AssertWorkspaceUntrustedAsync(factory, tmp.Path);
    }

    [Fact]
    public async Task Trust_endpoint_persistence_failure_returns_safe_error_without_trust()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var created = await CreateRunAsync(client, sessionId, tmp.Path);
        await AddTrustFailureTriggerAsync(tmp.DataPaths.AgentHarnessDatabase);

        using var response = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{created.GetProperty("run_id").GetString()}/trust",
            new
            {
                trust_request_id = created.GetProperty("trust_request_id").GetString(),
                display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
                decision = "approve",
            });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("workspace_trust_store_failed", body.GetProperty("error_code").GetString());
        await AssertWorkspaceUntrustedAsync(factory, tmp.Path);
        using var runResponse = await client.GetAsync(
            $"/agent/v1/runs/{created.GetProperty("run_id").GetString()}");
        var runBody = await runResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("waiting_workspace_trust", runBody.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Trust_endpoint_rejects_stale_request_after_denial()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var created = await CreateRunAsync(client, sessionId, tmp.Path);
        var oldRequestId = created.GetProperty("trust_request_id").GetString()!;
        var resolver = factory.Services.GetRequiredService<WindowsWorkspaceIdentityResolver>();
        var store = factory.Services.GetRequiredService<SqliteWorkspaceStore>();
        var trustStore = factory.Services.GetRequiredService<WorkspaceTrustRequestStore>();
        var identity = await resolver.ResolveAsync(tmp.Path);
        var stored = await store.GetAsync(identity.WorkspaceId, CancellationToken.None);
        Assert.NotNull(stored);
        var denial = await trustStore.ApplyAsync(
            new WorkspaceTrustResponse(
                new TrustRequestId(oldRequestId),
                identity,
                identity.DisplayIdentityHash,
                WorkspaceTrustDecision.Deny),
            identity,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.True(denial.Accepted);
        var fresh = await factory.Services.GetRequiredService<WorkspaceTrustService>()
            .EnsureTrustRequestAsync(tmp.Path);
        Assert.NotEqual(oldRequestId, fresh.RequestId.Value);

        using var response = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{created.GetProperty("run_id").GetString()}/trust",
            new
            {
                trust_request_id = oldRequestId,
                display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
                decision = "approve",
            });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("trust_stale", body.GetProperty("error_code").GetString());
        await AssertWorkspaceUntrustedAsync(factory, tmp.Path);
    }

    [Fact]
    public async Task Trust_endpoint_rejects_moved_identity_without_approving_new_root()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var created = await CreateRunAsync(client, sessionId, tmp.Path);
        var oldRequestId = created.GetProperty("trust_request_id").GetString()!;
        var resolver = factory.Services.GetRequiredService<WindowsWorkspaceIdentityResolver>();
        var store = factory.Services.GetRequiredService<SqliteWorkspaceStore>();
        var identity = await resolver.ResolveAsync(tmp.Path);
        var movedIdentity = new WorkspaceIdentity(
            identity.WorkspaceId,
            identity.VolumeSerialNumber + "-moved",
            identity.RootFileId + "-moved",
            new string('f', 64));
        var movedRequestId = new TrustRequestId("trust_" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        await store.RegisterAsync(
            new WorkspaceRegistration(
                movedIdentity,
                new WorkspaceRootCiphertext(new byte[12], new byte[] { 1 }, new byte[16])),
            new WorkspaceTrustRequest(movedRequestId, movedIdentity, now, now.AddMinutes(10)),
            CancellationToken.None);

        using var response = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{created.GetProperty("run_id").GetString()}/trust",
            new
            {
                trust_request_id = oldRequestId,
                display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
                decision = "approve",
            });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var stored = await store.GetAsync(identity.WorkspaceId, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("trust_stale", body.GetProperty("error_code").GetString());
        Assert.NotNull(stored);
        Assert.Equal(movedIdentity, stored!.Identity);
        Assert.False(stored.IsTrusted);
        Assert.Null(stored.TrustSource);
    }

    [Fact]
    public async Task Trust_approval_revalidates_live_identity_before_persisting()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var created = await CreateRunAsync(client, sessionId, tmp.Path);
        var runId = created.GetProperty("run_id").GetString()!;
        var resolver = factory.Services.GetRequiredService<WindowsWorkspaceIdentityResolver>();
        var originalIdentity = await resolver.ResolveAsync(tmp.Path);
        var movedPath = Path.Combine(
            Path.GetDirectoryName(tmp.Path)!,
            "moved-" + Guid.NewGuid().ToString("N"));
        Directory.Move(tmp.Path, movedPath);
        Directory.CreateDirectory(tmp.Path);
        var recreatedIdentity = await resolver.ResolveAsync(tmp.Path);
        Assert.NotEqual(originalIdentity, recreatedIdentity);

        using var response = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{runId}/trust",
            new
            {
                trust_request_id = created.GetProperty("trust_request_id").GetString(),
                display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
                decision = "approve",
            });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("workspace_identity_changed", body.GetProperty("error_code").GetString());
        await AssertWorkspaceTrustStateAsync(
            factory,
            originalIdentity,
            expectedDecision: null,
            expectedSource: null);
        Assert.Equal(0, await CountOutboxAsync(tmp.DataPaths.AgentHarnessDatabase, runId));
        using var runResponse = await client.GetAsync($"/agent/v1/runs/{runId}");
        var runBody = await runResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("waiting_workspace_trust", runBody.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("approve", "waiting_history", "waiting_history", 1)]
    [InlineData("deny", "cancelled", "cancelled", 0)]
    public async Task Trust_retry_repairs_transition_after_run_commit_failure(
        string decision,
        string expectedStatus,
        string expectedSafeCode,
        int expectedOutboxCount)
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var created = await CreateRunAsync(client, sessionId, tmp.Path);
        var runId = created.GetProperty("run_id").GetString()!;
        await AddRunCommitFailureGateAsync(tmp.DataPaths.AgentHarnessDatabase);

        using var firstResponse = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{runId}/trust",
            CreateTrustPayload(created, decision));
        var firstBodyText = await firstResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, firstResponse.StatusCode);
        var firstBody = JsonDocument.Parse(firstBodyText).RootElement;
        Assert.Equal("commit_conflict", firstBody.GetProperty("error_code").GetString());
        await AssertWorkspaceTrustStateAsync(
            factory,
            await ResolveIdentityAsync(factory, tmp.Path),
            decision == "approve" ? WorkspaceTrustDecision.Approve : WorkspaceTrustDecision.Deny,
            decision == "approve" ? "explicit" : null);
        Assert.Equal(0, await CountOutboxAsync(tmp.DataPaths.AgentHarnessDatabase, runId));
        Assert.Equal(
            0,
            await CountSafeEventAsync(tmp.DataPaths.AgentHarnessDatabase, runId, expectedSafeCode));
        using var waitingResponse = await client.GetAsync($"/agent/v1/runs/{runId}");
        var waitingBody = await waitingResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("waiting_workspace_trust", waitingBody.GetProperty("status").GetString());
        Assert.Equal(
            created.GetProperty("trust_request_id").GetString(),
            waitingBody.GetProperty("trust_request_id").GetString());
        Assert.Equal(
            created.GetProperty("display_identity_hash").GetString(),
            waitingBody.GetProperty("display_identity_hash").GetString());

        await ClearRunCommitFailureGateAsync(tmp.DataPaths.AgentHarnessDatabase);
        using var retryResponse = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{runId}/trust",
            CreateTrustPayload(created, decision));
        var retryBody = await retryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        Assert.Equal(expectedStatus, retryBody.GetProperty("status").GetString());
        Assert.Equal(expectedOutboxCount, await CountOutboxAsync(tmp.DataPaths.AgentHarnessDatabase, runId));
        Assert.Equal(
            1,
            await CountSafeEventAsync(tmp.DataPaths.AgentHarnessDatabase, runId, expectedSafeCode));

        using var replayResponse = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{runId}/trust",
            CreateTrustPayload(created, decision));
        var replayBody = await replayResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var expectedReplayStatus = decision == "approve" ? "waiting_history_user" : expectedStatus;
        if (expectedReplayStatus != replayBody.GetProperty("status").GetString())
        {
            using var failedRunResponse = await client.GetAsync($"/agent/v1/runs/{runId}");
            var failedRunBody = await failedRunResponse.Content.ReadAsStringAsync();
            Assert.Fail($"Trust replay expected {expectedReplayStatus}; actual replay={replayBody.GetRawText()}; run={failedRunBody}");
        }
        Assert.Equal(expectedReplayStatus, replayBody.GetProperty("status").GetString());
        Assert.Equal(expectedOutboxCount, await CountOutboxAsync(tmp.DataPaths.AgentHarnessDatabase, runId));
        Assert.Equal(
            1,
            await CountSafeEventAsync(tmp.DataPaths.AgentHarnessDatabase, runId, expectedSafeCode));
    }

    [Fact]
    public async Task Concurrent_trust_retries_commit_one_transition_and_one_outbox()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var created = await CreateRunAsync(client, sessionId, tmp.Path);
        var runId = created.GetProperty("run_id").GetString()!;
        await AddRunCommitFailureGateAsync(tmp.DataPaths.AgentHarnessDatabase);

        using var firstResponse = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{runId}/trust",
            CreateTrustPayload(created, "approve"));
        Assert.Equal(HttpStatusCode.Conflict, firstResponse.StatusCode);
        await ClearRunCommitFailureGateAsync(tmp.DataPaths.AgentHarnessDatabase);

        var retryTasks = new[]
        {
            client.PostAsJsonAsync($"/agent/v1/runs/{runId}/trust", CreateTrustPayload(created, "approve")),
            client.PostAsJsonAsync($"/agent/v1/runs/{runId}/trust", CreateTrustPayload(created, "approve")),
        };
        var retryResponses = await Task.WhenAll(retryTasks);

        Assert.Contains(retryResponses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(
            retryResponses,
            response => Assert.Contains(
                response.StatusCode,
                new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(1, await CountOutboxAsync(tmp.DataPaths.AgentHarnessDatabase, runId));
        Assert.Equal(
            1,
            await CountSafeEventAsync(tmp.DataPaths.AgentHarnessDatabase, runId, "waiting_history"));
        foreach (var response in retryResponses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Workspace_concurrency_endpoint_validates_identity_and_reports_missing_workspace()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var invalid = await client.GetAsync("/agent/v1/workspaces/not-a-workspace/concurrency");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var missing = await client.GetAsync("/agent/v1/workspaces/ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/concurrency");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var json = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workspace_not_found", json.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Policy_routes_are_active_after_task_9()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var getPol = await client.GetAsync("/agent/v1/policies");
        Assert.Equal(200, (int)getPol.StatusCode);
        var json = await getPol.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("items", out _));
        // POST with valid global rule but without interactive should be 403, not 409
        using var postPol = await client.PostAsJsonAsync("/agent/v1/policies", new { scope = "global", decision = "allow", matcher = new { tool_id = "files.read" } });
        Assert.Equal((int)HttpStatusCode.Forbidden, (int)postPol.StatusCode);
        var postJson = await postPol.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("interactive_required", postJson.GetProperty("error_code").GetString());
    }

    private static string FindContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "backend-dotnet", "public-route-contract.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("contract not found");
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private static object CreateTrustPayload(JsonElement created, string decision) => new
    {
        trust_request_id = created.GetProperty("trust_request_id").GetString(),
        display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
        decision,
    };

    private static async Task AddRunCommitFailureGateAsync(string databasePath)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS trust_retry_gate (fail_next INTEGER NOT NULL);
            DELETE FROM trust_retry_gate;
            INSERT INTO trust_retry_gate(fail_next) VALUES (1);
            CREATE TRIGGER IF NOT EXISTS fail_workspace_trust_run_commit
            BEFORE UPDATE OF state ON agent_runs
            WHEN EXISTS (SELECT 1 FROM trust_retry_gate WHERE fail_next = 1)
            BEGIN
                SELECT RAISE(ABORT, 'run commit persistence failure');
            END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ClearRunCommitFailureGateAsync(string databasePath)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM trust_retry_gate";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountOutboxAsync(string databasePath, string runId)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_conversation_outbox WHERE run_id = $run_id";
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountSafeEventAsync(
        string databasePath,
        string runId,
        string safeCode)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_events WHERE run_id = $run_id AND safe_code = $safe_code";
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$safe_code", safeCode);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task AddTrustFailureTriggerAsync(string databasePath)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fail_workspace_trust_approval
            BEFORE UPDATE OF trust_decision ON agent_workspaces
            WHEN NEW.trust_decision = 0
            BEGIN
                SELECT RAISE(ABORT, 'workspace trust persistence failure');
            END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<JsonElement> CreateRunAsync(
        HttpClient client,
        string sessionId,
        string workspacePath)
    {
        Directory.CreateDirectory(workspacePath);
        using var response = await client.PostAsJsonAsync(
            "/agent/v1/runs",
            new
            {
                session_id = sessionId,
                workspace_path = workspacePath,
                prompt = "trust flow",
                preset = "standard",
                idempotency_key = "trust-key-" + Guid.NewGuid().ToString("N"),
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Create run failed {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task AssertWorkspaceUntrustedAsync(
        BackendWebApplicationFactory factory,
        string workspacePath)
    {
        var identity = await ResolveIdentityAsync(factory, workspacePath);
        await AssertWorkspaceTrustStateAsync(factory, identity, expectedDecision: null, expectedSource: null);
    }

    private static async Task AssertWorkspaceTrustStateAsync(
        BackendWebApplicationFactory factory,
        WorkspaceIdentity identity,
        WorkspaceTrustDecision? expectedDecision,
        string? expectedSource)
    {
        var store = factory.Services.GetRequiredService<SqliteWorkspaceStore>();
        var stored = await store.GetAsync(identity.WorkspaceId, CancellationToken.None);
        Assert.NotNull(stored);
        var expectedTrustSource = expectedSource == "explicit"
            ? WorkspaceTrustSource.Explicit
            : (WorkspaceTrustSource?)null;
        var expectedIsTrusted = expectedDecision == WorkspaceTrustDecision.Approve
            && expectedTrustSource == WorkspaceTrustSource.Explicit;
        Assert.Equal(expectedDecision, stored!.TrustRequest.AppliedDecision);
        Assert.Equal(expectedTrustSource, stored.TrustSource);
        Assert.Equal(expectedIsTrusted, stored.IsTrusted);
    }

    private static Task<WorkspaceIdentity> ResolveIdentityAsync(
        BackendWebApplicationFactory factory,
        string workspacePath) =>
        factory.Services.GetRequiredService<WindowsWorkspaceIdentityResolver>()
            .ResolveAsync(workspacePath);

    private static SettingsSnapshot CreateSettingsSnapshot(string backend, string model) => new(
        new JsonObject(),
        backend,
        model,
        CustomModel: string.Empty,
        CustomUrl: "http://localhost:8000/v1",
        OpenAiModel: backend == "OpenAI" ? model : string.Empty,
        AnthropicModel: backend == "Anthropic" ? model : string.Empty);

    private sealed class StubSettingsStore(SettingsSnapshot snapshot) : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken) => Task.CompletedTask;
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
