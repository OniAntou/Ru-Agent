using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentV1EndpointTests
{
    private static readonly (string Method, string Path)[] ExpectedRoutes =
    {
        ("POST", "/agent/v1/runs"),
        ("GET", "/agent/v1/runs/{runId}"),
        ("GET", "/agent/v1/runs/{runId}/events"),
        ("GET", "/agent/v1/runs/{runId}/payloads/{payloadId}"),
        ("POST", "/agent/v1/runs/{runId}/approvals/{approvalId}"),
        ("POST", "/agent/v1/runs/{runId}/trust"),
        ("POST", "/agent/v1/runs/{runId}/cancel"),
        ("POST", "/agent/v1/runs/{runId}/continue"),
        ("POST", "/agent/v1/runs/{runId}/compact"),
        ("POST", "/agent/v1/runs/{runId}/commands"),
        ("POST", "/agent/v1/runs/{runId}/reconcile"),
        ("GET", "/agent/v1/sessions/{sessionId}/runs"),
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
        Assert.Equal(62, doc.RootElement.GetArrayLength() - 1); // minus 1 websocket, HTTP count 62 (41 + 20 Agent + POST /host/shutdown)
    }

    [Theory]
    [InlineData("POST", "/agent/v1/runs")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/events")]
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/payloads/payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approvals/apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/trust")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/cancel")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/continue")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/compact")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/commands")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/reconcile")]
    [InlineData("GET", "/agent/v1/sessions/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/runs")]
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
    [InlineData("GET", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/payloads/payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/approvals/apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/trust")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/cancel")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/continue")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/compact")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/commands")]
    [InlineData("POST", "/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/reconcile")]
    [InlineData("GET", "/agent/v1/sessions/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/runs")]
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
    public async Task Create_run_rejects_legacy_image_attachments_instead_of_dropping_them()
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
                idempotency_key = "image-attachment-test",
                image_attachments = new[] { "data:image/png;base64,abc" },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("image_attachments_unsupported", json.GetProperty("error_code").GetString());
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
}
