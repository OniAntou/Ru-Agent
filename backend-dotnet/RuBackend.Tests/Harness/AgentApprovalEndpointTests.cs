using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentApprovalEndpointTests
{
    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private static string CreateTempWorkspace(out RuBackend.Business.DataPaths dataPaths)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-approval-test-" + Guid.NewGuid().ToString("N"));
        var ws = Path.Combine(root, "ws");
        Directory.CreateDirectory(ws);
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(dataRoot);
        dataPaths = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, Path.Combine(root, "logs"));
        return ws;
    }

    [Fact]
    public async Task Approval_404_RunNotFound()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var fakeRunId = "run_" + new string('a', 32);
        var approvalId = "apr_" + new string('b', 32);
        using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{fakeRunId}/approvals/{approvalId}", new { scope = "once", decision = "allow" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("run_not_found", json.GetProperty("error_code").GetString());
        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task Approval_FirstWriterWins_SecondGetsApprovalStale()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "approval test",
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        var runId = (await runResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;
        // Clear ledger before
        RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
        var approvalId = "apr_" + new string('c', 32);

        using var resp1 = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { scope = "once", decision = "allow" });
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var json1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorized", json1.GetProperty("status").GetString());

        using var resp2 = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { scope = "once", decision = "allow" });
        Assert.Equal((HttpStatusCode)409, resp2.StatusCode);
        var json2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approval_stale", json2.GetProperty("error_code").GetString());
        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task Approval_ExactScopePreview_StripsGlob()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "preview test",
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        var runId = (await runResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;
        RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
        var approvalId = "apr_" + new string('d', 32);

        using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { scope = "run", decision = "allow" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("matcher_preview", out var preview), $"missing matcher_preview in {json.GetRawText()}");
        var previewText = preview.GetRawText();
        Assert.DoesNotContain("*", previewText);
        Assert.True(json.TryGetProperty("rule_saved", out var ruleSaved), $"missing rule_saved in {json.GetRawText()}");
        // For run scope, rule_saved should be true (if persistence succeeds)
        Assert.True(ruleSaved.ValueKind == JsonValueKind.True, $"Expected rule_saved true but got {ruleSaved} json:{json.GetRawText()}");
        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task Approval_Deny_DoesNotCreateRule_AndReturnsDenied()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "deny test",
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        var runId = (await runResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;
        RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
        var approvalId = "apr_" + new string('e', 32);

        using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { decision = "deny", scope = "once" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("denied", json.GetProperty("status").GetString());
        Assert.False(json.TryGetProperty("rule_saved", out var rs) && rs.GetBoolean());

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task Approval_ProjectScope_RuleSaved()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "project scope",
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        var runId = (await runResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;
        RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
        var approvalId = "apr_" + new string('f', 32);

        using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { scope = "project", decision = "allow" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorized", json.GetProperty("status").GetString());
        Assert.True(json.TryGetProperty("rule_saved", out var rs2) && rs2.ValueKind == JsonValueKind.True, $"Expected rule_saved true but json: {json.GetRawText()}");
        Assert.True(json.TryGetProperty("rule_id", out var rid) && !string.IsNullOrWhiteSpace(rid.GetString()));

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task Approval_OnceScope_DoesNotPersistReusableRule()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "once scope",
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        var runId = (await runResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;
        RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
        var approvalId = "apr_" + new string('a', 32).Replace('a', '9');

        using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { scope = "once", decision = "allow" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorized", json.GetProperty("status").GetString());
        // rule_saved should be false for once (no reusable)
        if (json.TryGetProperty("rule_saved", out var rs))
            Assert.Equal(JsonValueKind.False, rs.ValueKind);

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    private static void SqliteConnectionClear()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
    }
}
