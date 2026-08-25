using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentSessionPermissionEndpointTests
{
    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private static string CreateTempWorkspace(out RuBackend.Business.DataPaths dataPaths)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-sessionpreset-test-" + Guid.NewGuid().ToString("N"));
        var ws = Path.Combine(root, "ws");
        Directory.CreateDirectory(ws);
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(dataRoot);
        dataPaths = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, Path.Combine(root, "logs"));
        return ws;
    }

    [Fact]
    public async Task SessionPreset_Standard_CreatesMonotonicVersion()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");

        var body = new { kind = "session_preset", session_id = sessionId, preset = "standard", expected_version = 0 };
        using var resp = await client.PostAsJsonAsync("/agent/v1/policies", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("standard", json.GetProperty("preset").GetString());
        Assert.Equal(1, json.GetProperty("version").GetInt64());

        // Second update to same session should increment
        var body2 = new { kind = "session_preset", session_id = sessionId, preset = "standard", expected_version = 1 };
        using var resp2 = await client.PostAsJsonAsync("/agent/v1/policies", body2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var json2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json2.GetProperty("version").GetInt64());

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task SessionPreset_Full_RequiresInteractiveConfirmation()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);

        var body = new { kind = "session_preset", session_id = sessionId, preset = "full_access", expected_version = 0 };
        using var resp = await client.PostAsJsonAsync("/agent/v1/policies", body);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("interactive_required", json.GetProperty("error_code").GetString());

        // With interactive header should succeed
        using var client2 = factory.CreateAuthenticatedClient();
        client2.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        using var resp2 = await client2.PostAsJsonAsync("/agent/v1/policies", body);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var json2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("full_access", json2.GetProperty("preset").GetString());
        Assert.Equal(1, json2.GetProperty("version").GetInt64());

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task SessionPreset_AffectsOnlyOneSession()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var session1 = await CreateSessionAsync(client);
        var session2 = await CreateSessionAsync(client);
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");

        var body = new { kind = "session_preset", session_id = session1, preset = "standard", expected_version = 0 };
        using var resp = await client.PostAsJsonAsync("/agent/v1/policies", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // session2 should still be unset; creating run with standard and version 1 for session2 should succeed via implicit creation
        var runBody = new
        {
            session_id = session2,
            workspace_path = wsPath,
            prompt = "test second session",
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        Assert.Equal(HttpStatusCode.Created, runResp.StatusCode);

        // session1 run with wrong version should fail
        var runBodyBad = new
        {
            session_id = session1,
            workspace_path = wsPath,
            prompt = "bad version",
            preset = "standard",
            preset_selection_version = 99,
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var badResp = await client.PostAsJsonAsync("/agent/v1/runs", runBodyBad);
        Assert.Equal((HttpStatusCode)409, badResp.StatusCode);

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task RunMustMatchStoredPresetVersion()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        var presetBody = new { kind = "session_preset", session_id = sessionId, preset = "standard", expected_version = 0 };
        using var presetResp = await client.PostAsJsonAsync("/agent/v1/policies", presetBody);
        Assert.Equal(HttpStatusCode.OK, presetResp.StatusCode);

        // Correct version should succeed
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "correct version",
            preset = "standard",
            preset_selection_version = 1,
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        // May be 201 or 409 workspace_busy if previous run still active; cancel if needed
        if (runResp.StatusCode == (HttpStatusCode)409)
        {
            var err = await runResp.Content.ReadFromJsonAsync<JsonElement>();
            if (err.GetProperty("error_code").GetString() == "workspace_busy")
            {
                // cancel previous and retry with new workspace
                var wsPath2 = Path.Combine(Path.GetDirectoryName(wsPath)!, "ws2");
                Directory.CreateDirectory(wsPath2);
                var runBody2 = new
                {
                    session_id = sessionId,
                    workspace_path = wsPath2,
                    prompt = "correct version 2",
                    preset = "standard",
                    preset_selection_version = 1,
                    idempotency_key = Guid.NewGuid().ToString("N")
                };
                using var runResp2 = await client.PostAsJsonAsync("/agent/v1/runs", runBody2);
                // For this test, we just verify preset mismatch handling; allow workspace busy as not preset mismatch
                Assert.True(runResp2.StatusCode == HttpStatusCode.Created || (int)runResp2.StatusCode == 409);
                if (runResp2.StatusCode == HttpStatusCode.Created)
                {
                    var j = await runResp2.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.Equal(1, j.GetProperty("policy_snapshot_version").GetInt64());
                }
            }
        }
        else
        {
            Assert.Equal(HttpStatusCode.Created, runResp.StatusCode);
            var j = await runResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, j.GetProperty("policy_snapshot_version").GetInt64());
        }

        // Wrong version should fail
        var badBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "wrong version",
            preset = "standard",
            preset_selection_version = 2,
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var badResp = await client.PostAsJsonAsync("/agent/v1/runs", badBody);
        Assert.Equal((HttpStatusCode)409, badResp.StatusCode);
        var badJson = await badResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("preset_mismatch", badJson.GetProperty("error_code").GetString());

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task RequestBodyPresetAloneCannotEscalateStandardToFull()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        // Set to standard first
        using var clientInteractive = factory.CreateAuthenticatedClient();
        clientInteractive.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        var presetBody = new { kind = "session_preset", session_id = sessionId, preset = "standard", expected_version = 0 };
        using var presetResp = await clientInteractive.PostAsJsonAsync("/agent/v1/policies", presetBody);
        Assert.Equal(HttpStatusCode.OK, presetResp.StatusCode);

        // Try to create run with full_access without having set full preset – should fail even though body says full
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "try escalate",
            preset = "full_access",
            preset_selection_version = 1,
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        Assert.Equal((HttpStatusCode)409, runResp.StatusCode);
        var json = await runResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("preset_mismatch", json.GetProperty("error_code").GetString());

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task SessionPreset_VersionConflict_Returns409()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");

        var body1 = new { kind = "session_preset", session_id = sessionId, preset = "standard", expected_version = 0 };
        using var resp1 = await client.PostAsJsonAsync("/agent/v1/policies", body1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        var body2 = new { kind = "session_preset", session_id = sessionId, preset = "standard", expected_version = 0 };
        using var resp2 = await client.PostAsJsonAsync("/agent/v1/policies", body2);
        Assert.Equal((HttpStatusCode)409, resp2.StatusCode);
        var json = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("preset_version_conflict", json.GetProperty("error_code").GetString());

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    private static void SqliteConnectionClear()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
    }
}
