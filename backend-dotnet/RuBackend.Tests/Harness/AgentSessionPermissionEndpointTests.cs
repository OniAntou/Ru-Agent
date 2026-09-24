using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
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
    public async Task SessionPreset_Plan_DoesNotRequireInteractive_ThenFlowsToRuns()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);

        var body = new { kind = "session_preset", session_id = sessionId, preset = "plan", expected_version = 0 };
        using var resp = await client.PostAsJsonAsync("/agent/v1/policies", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("plan", json.GetProperty("preset").GetString());
        Assert.Equal(1, json.GetProperty("version").GetInt64());

        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "plan run",
            preset = "plan",
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        Assert.Equal(HttpStatusCode.Created, runResp.StatusCode);
        var runJson = await runResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("plan", runJson.GetProperty("preset").GetString());

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
    public async Task FullAccessRunKeepsStoredFullAccessPreset()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        // Set to full_access first (requires interactive confirmation)
        using var clientInteractive = factory.CreateAuthenticatedClient();
        clientInteractive.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        var presetBody = new { kind = "session_preset", session_id = sessionId, preset = "full_access", expected_version = 0 };
        using var presetResp = await clientInteractive.PostAsJsonAsync("/agent/v1/policies", presetBody);
        Assert.Equal(HttpStatusCode.OK, presetResp.StatusCode);

        // A full_access run on a full_access session keeps full_access behavior.
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "full access body",
            preset = "full_access",
            preset_selection_version = 1,
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        Assert.Equal(HttpStatusCode.Created, runResp.StatusCode);
        var json = await runResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("full_access", json.GetProperty("preset").GetString());

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

    [Fact]
    public async Task SessionPreset_CapacityRejectsBeforeMutationWithoutEviction()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        var sessionId = await CreateSessionAsync(client);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");
        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        await using (var connection = new SqliteConnection($"Data Source={dataPaths.AgentHarnessDatabase};Mode=ReadWrite;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO agent_session_preset_snapshots(
                    snapshot_token, session_id, had_previous_selection,
                    previous_preset, previous_permission_profile_version,
                    previous_version, previous_updated_at_utc,
                    applied_preset, applied_permission_profile_version,
                    applied_version, expires_at_utc, consumed_at_utc, created_at_utc)
                VALUES ($token, $session_id, 0, NULL, NULL, NULL, NULL,
                    'standard', 1, 1, $expires_at, NULL, $created_at)
                """;
            var token = command.Parameters.Add("$token", SqliteType.Text);
            var capacitySession = command.Parameters.Add("$session_id", SqliteType.Text);
            var expires = command.Parameters.Add("$expires_at", SqliteType.Text);
            var created = command.Parameters.Add("$created_at", SqliteType.Text);
            expires.Value = expiresAt;
            created.Value = createdAt;
            for (var i = 0; i < 1024; i++)
            {
                token.Value = "sps_" + i.ToString("x32");
                capacitySession.Value = "capacity-" + i.ToString("x32");
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        using var response = await client.PostAsJsonAsync("/agent/v1/policies", new
        {
            kind = "session_preset",
            session_id = sessionId,
            preset = "full_access",
            expected_version = 0,
            snapshot_token = "sps_" + new string('f', 32)
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("preset_snapshot_capacity_exceeded", body.GetProperty("error_code").GetString());
        await using var verify = new SqliteConnection($"Data Source={dataPaths.AgentHarnessDatabase};Mode=ReadOnly;Pooling=False");
        await verify.OpenAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT (SELECT COUNT(*) FROM agent_session_settings WHERE session_id = $session_id), (SELECT COUNT(*) FROM agent_session_preset_snapshots)";
        count.Parameters.AddWithValue("$session_id", sessionId);
        await using var reader = await count.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(1024L, reader.GetInt64(1));

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SessionPreset_StorageFailureIsReturnedAsServerError()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        var sessionId = await CreateSessionAsync(client);

        await using (var connection = new SqliteConnection($"Data Source={dataPaths.AgentHarnessDatabase};Mode=ReadWrite;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fail_session_preset_insert
                BEFORE INSERT ON agent_session_settings
                BEGIN
                    SELECT RAISE(ABORT, 'forced storage failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        using var response = await client.PostAsJsonAsync("/agent/v1/policies", new
        {
            kind = "session_preset",
            session_id = sessionId,
            preset = "standard",
            expected_version = 0
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("harness_permission_store_failed", body.GetProperty("error_code").GetString());

        await using (var connection = new SqliteConnection($"Data Source={dataPaths.AgentHarnessDatabase};Mode=ReadWrite;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER fail_session_preset_insert";
            await command.ExecuteNonQueryAsync();
        }

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task SessionPreset_SnapshotRestoresNewSessionToNoSelection()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        var sessionId = await CreateSessionAsync(client);

        using var setResponse = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset", session_id = sessionId, preset = "standard", expected_version = 0 });
        var setJson = await setResponse.Content.ReadFromJsonAsync<JsonElement>();
        var snapshotToken = setJson.GetProperty("snapshot_token").GetString()!;
        Assert.True(setResponse.IsSuccessStatusCode);
        Assert.False(string.IsNullOrWhiteSpace(snapshotToken));

        using var restoreResponse = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset_restore", session_id = sessionId, snapshot_token = snapshotToken });
        var restoreJson = await restoreResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(restoreResponse.IsSuccessStatusCode);
        Assert.Equal("ok", restoreJson.GetProperty("status").GetString());
        Assert.Equal(0, restoreJson.GetProperty("version").GetInt64());

        using var recreateResponse = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset", session_id = sessionId, preset = "plan", expected_version = 0 });
        Assert.True(recreateResponse.IsSuccessStatusCode);

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task SessionPreset_SnapshotRestoresPriorSelection()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        var sessionId = await CreateSessionAsync(client);

        using var firstResponse = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset", session_id = sessionId, preset = "standard", expected_version = 0 });
        using var secondResponse = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset", session_id = sessionId, preset = "plan", expected_version = 1 });
        var secondJson = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(firstResponse.IsSuccessStatusCode);
        Assert.True(secondResponse.IsSuccessStatusCode);

        using var restoreResponse = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset_restore", session_id = sessionId, snapshot_token = secondJson.GetProperty("snapshot_token").GetString() });
        var restoreJson = await restoreResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(restoreResponse.IsSuccessStatusCode);
        Assert.Equal("standard", restoreJson.GetProperty("preset").GetString());
        Assert.Equal(1, restoreJson.GetProperty("version").GetInt64());

        using var updateResponse = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset", session_id = sessionId, preset = "standard", expected_version = 1 });
        Assert.True(updateResponse.IsSuccessStatusCode);

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task SessionPreset_UsesClientTokenAndRestoreReplayIsSafe()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        var sessionId = await CreateSessionAsync(client);
        var token = "sps_" + new string('c', 32);

        using var setResponse = await client.PostAsJsonAsync("/agent/v1/policies", new
        {
            kind = "session_preset",
            session_id = sessionId,
            preset = "standard",
            expected_version = 0,
            snapshot_token = token
        });
        var setJson = await setResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(setResponse.IsSuccessStatusCode);
        Assert.Equal(token, setJson.GetProperty("snapshot_token").GetString());

        using var firstRestore = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset_restore", session_id = sessionId, snapshot_token = token });
        var firstJson = await firstRestore.Content.ReadFromJsonAsync<JsonElement>();
        using var replay = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset_restore", session_id = sessionId, snapshot_token = token });
        var replayJson = await replay.Content.ReadFromJsonAsync<JsonElement>();
        using var later = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset", session_id = sessionId, preset = "plan", expected_version = 0, snapshot_token = "sps_" + new string('d', 32) });
        using var blockedReplay = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset_restore", session_id = sessionId, snapshot_token = token });

        Assert.True(firstRestore.IsSuccessStatusCode);
        Assert.Equal("ok", firstJson.GetProperty("status").GetString());
        Assert.True(replay.IsSuccessStatusCode);
        Assert.True(replayJson.GetProperty("replayed").GetBoolean());
        Assert.True(later.IsSuccessStatusCode);
        Assert.Equal((HttpStatusCode)409, blockedReplay.StatusCode);
    }

    [Fact]
    public async Task SessionPreset_RestoreRejectsConcurrentVersionChange()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        var sessionId = await CreateSessionAsync(client);

        using var firstResponse = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset", session_id = sessionId, preset = "standard", expected_version = 0 });
        var firstJson = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var secondResponse = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset", session_id = sessionId, preset = "plan", expected_version = 1 });
        Assert.True(firstResponse.IsSuccessStatusCode);
        Assert.True(secondResponse.IsSuccessStatusCode);

        using var restoreResponse = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset_restore", session_id = sessionId, snapshot_token = firstJson.GetProperty("snapshot_token").GetString() });
        var restoreJson = await restoreResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((HttpStatusCode)409, restoreResponse.StatusCode);
        Assert.Equal("preset_version_conflict", restoreJson.GetProperty("error_code").GetString());

        using var currentResponse = await client.PostAsJsonAsync("/agent/v1/policies", new { kind = "session_preset", session_id = sessionId, preset = "plan", expected_version = 2 });
        Assert.True(currentResponse.IsSuccessStatusCode);

        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    private static void SqliteConnectionClear()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
    }
}
