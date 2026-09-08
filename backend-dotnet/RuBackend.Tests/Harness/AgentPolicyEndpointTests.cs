using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentPolicyEndpointTests
{
    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private static string CreateTempWorkspace(out RuBackend.Business.DataPaths dataPaths)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-policy-test-" + Guid.NewGuid().ToString("N"));
        var ws = Path.Combine(root, "ws");
        Directory.CreateDirectory(ws);
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(dataRoot);
        dataPaths = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, Path.Combine(root, "logs"));
        return ws;
    }

    [Fact]
    public async Task ListPolicies_InitiallyEmpty()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();

        using var resp = await client.GetAsync("/agent/v1/policies");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("items", out var items));
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(0, items.GetArrayLength());
        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task CreateGlobalPolicy_WithoutInteractive_Returns403()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var body = new
        {
            scope = "global",
            decision = "allow",
            matcher = new { tool_id = "files.read", canonical_path_pattern = "src/secret.ts" }
        };
        using var resp = await client.PostAsJsonAsync("/agent/v1/policies", body);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("interactive_required", json.GetProperty("error_code").GetString());
        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task CreateGlobalPolicy_WithInteractive_SucceedsAndListReturnsIt()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");

        var body = new
        {
            scope = "global",
            decision = "allow",
            matcher = new { tool_id = "files.read", canonical_path_pattern = "src/secret.ts" }
        };
        using var resp = await client.PostAsJsonAsync("/agent/v1/policies", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ruleId = json.GetProperty("rule_id").GetString()!;
        Assert.StartsWith("rule_", ruleId);

        using var listResp = await client.GetAsync("/agent/v1/policies");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listJson = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        var items = listJson.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(ruleId, items[0].GetProperty("rule_id").GetString());

        // Verify encrypted at rest: raw DB should not contain plaintext path
        // Use raw connection to check agent_global_rules
        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task CreatePolicy_EncryptsSensitiveValuesAtRest()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");

        var sensitivePath = "D:\\sensitive\\path\\secret.ts";
        var sensitiveArgv = "super-secret-argv-value-xyz";
        var body = new
        {
            scope = "global",
            decision = "allow",
            matcher = new { canonical_path_pattern = sensitivePath, argv_pattern = new[] { sensitiveArgv } }
        };
        using var resp = await client.PostAsJsonAsync("/agent/v1/policies", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // Verify via direct DB access that raw document does not contain plaintext
        var dbPath = dataPaths.AgentHarnessDatabase;
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rule_document FROM agent_global_rules LIMIT 1";
        var result = await cmd.ExecuteScalarAsync();
        var raw = (byte[])result!;
        var rawText = Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain(sensitivePath, rawText);
        Assert.DoesNotContain(sensitiveArgv, rawText);

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task CreatePolicy_RejectsUnknownField()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");

        var body = new
        {
            scope = "global",
            decision = "allow",
            matcher = new { tool_id = "files.read" },
            unknown_field = "oops"
        };
        using var resp = await client.PostAsJsonAsync("/agent/v1/policies", body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown_field", json.GetProperty("error_code").GetString());
        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task CreateProjectPolicy_RequiresWorkspaceId()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();

        // First create a run to get a valid workspace_id
        var sessionId = await CreateSessionAsync(client);
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "hello",
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        Assert.Equal(HttpStatusCode.Created, runResp.StatusCode);
        var runJson = await runResp.Content.ReadFromJsonAsync<JsonElement>();
        var workspaceId = runJson.GetProperty("workspace_id").GetString()!;

        var body = new
        {
            scope = "project",
            decision = "allow",
            matcher = new { tool_id = "files.read" },
            workspace_id = workspaceId
        };
        using var resp = await client.PostAsJsonAsync("/agent/v1/policies", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // Without workspace_id should fail
        var badBody = new
        {
            scope = "project",
            decision = "allow",
            matcher = new { tool_id = "files.read" }
        };
        using var badResp = await client.PostAsJsonAsync("/agent/v1/policies", badBody);
        Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task DeleteGlobalPolicy_WithoutInteractive_Fails_And_WithSucceeds()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        var body = new
        {
            scope = "global",
            decision = "allow",
            matcher = new { tool_id = "files.read" }
        };
        using var createResp = await client.PostAsJsonAsync("/agent/v1/policies", body);
        var ruleId = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rule_id").GetString()!;

        // Try delete without interactive header
        using var client2 = factory.CreateAuthenticatedClient();
        using var delResp1 = await client2.DeleteAsync($"/agent/v1/policies/{ruleId}");
        Assert.Equal(HttpStatusCode.Forbidden, delResp1.StatusCode);
        var errJson = await delResp1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("interactive_required", errJson.GetProperty("error_code").GetString());

        // With interactive should succeed
        using var client3 = factory.CreateAuthenticatedClient();
        client3.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        using var delResp2 = await client3.DeleteAsync($"/agent/v1/policies/{ruleId}");
        Assert.Equal(HttpStatusCode.OK, delResp2.StatusCode);

        // List should be empty again
        using var listResp = await client3.GetAsync("/agent/v1/policies");
        var listJson = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, listJson.GetProperty("items").GetArrayLength());

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task ApprovalCannotCreateGlobalRule()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "test",
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        var runId = (await runResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;
        var approvalId = "apr_" + new string('a', 32);

        var body = new { command = "global" };
        using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_approval_command", json.GetProperty("error_code").GetString());

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    private static void SqliteConnectionClear()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
    }
}
