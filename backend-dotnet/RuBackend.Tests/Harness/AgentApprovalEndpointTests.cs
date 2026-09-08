using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
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
    public async Task Approval_unknown_command_is_rejected_instead_of_defaulting_to_once()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var runId = "run_" + new string('a', 32);
        var approvalId = "apr_" + new string('b', 32);

        using var resp = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{runId}/approvals/{approvalId}",
            new { command = "not-a-command" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_approval_command", json.GetProperty("error_code").GetString());
        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task Approval_FabricatedId_RejectedAsApprovalStale()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        using var factory = new BackendWebApplicationFactory(dataPaths: dataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "fabricated id test",
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N")
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        var runId = (await runResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;
        var fakeApprovalId = "apr_" + new string('x', 32);

        using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{fakeApprovalId}", new { scope = "once", decision = "allow" });
        Assert.Equal((HttpStatusCode)409, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approval_stale", json.GetProperty("error_code").GetString());
        try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
        SqliteConnectionClear();
    }

    [Fact]
    public async Task Approval_FirstWriterWins_SecondGetsApprovalStale()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var (fact, client, runId, approvalId) = await StartWaitingApprovalRunAsync(wsPath, dataPaths);
            factory = fact;
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();

            using var resp1 = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { scope = "once", decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
            var json1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("authorized", json1.GetProperty("status").GetString());

            using var resp2 = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { scope = "once", decision = "allow" });
            Assert.Equal((HttpStatusCode)409, resp2.StatusCode);
            var json2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("approval_stale", json2.GetProperty("error_code").GetString());
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear();
        }
    }

    [Fact]
    public async Task Approval_RunScope_Rejected()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var (fact, client, runId, approvalId) = await StartWaitingApprovalRunAsync(wsPath, dataPaths);
            factory = fact;
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();

            using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { command = "allow_run" });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_approval_command", json.GetProperty("error_code").GetString());
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear();
        }
    }

    [Fact]
    public async Task Approval_Deny_DoesNotCreateRule_AndReturnsDenied()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var (fact, client, runId, approvalId) = await StartWaitingApprovalRunAsync(wsPath, dataPaths);
            factory = fact;
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();

            using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { decision = "deny", scope = "once" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("denied", json.GetProperty("status").GetString());
            Assert.Equal("approval_denied", json.GetProperty("error_code").GetString());
            Assert.Equal("failed", json.GetProperty("run_state").GetString());
            Assert.False(json.TryGetProperty("rule_saved", out var rs) && rs.GetBoolean());

            // Denial terminates the run; a stale reuse of the same ID is rejected.
            using var dup = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { decision = "deny", scope = "once" });
            Assert.Equal((HttpStatusCode)409, dup.StatusCode);
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear();
        }
    }

    [Fact]
    public async Task Approval_ProjectScope_Rejected()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var (fact, client, runId, approvalId) = await StartWaitingApprovalRunAsync(wsPath, dataPaths);
            factory = fact;
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();

            using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { command = "allow_project" });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_approval_command", json.GetProperty("error_code").GetString());

            using var runResp = await client.GetAsync($"/agent/v1/runs/{runId}");
            runResp.EnsureSuccessStatusCode();
            var workspaceId = (await runResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workspace_id").GetString()!;
            using var policies = await client.GetAsync($"/agent/v1/policies?scope=project&workspace_id={Uri.EscapeDataString(workspaceId)}");
            policies.EnsureSuccessStatusCode();
            var items = (await policies.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
            Assert.Equal(0, items.GetArrayLength());
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear();
        }
    }

    [Fact]
    public async Task External_folder_approval_never_writes_policy_rule()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        var dataRoot = Path.GetDirectoryName(wsPath)!;
        var externalPath = Path.Combine(dataRoot, "external");
        Directory.CreateDirectory(externalPath);
        var externalFile = Path.Combine(externalPath, "note.txt");
        File.WriteAllText(externalFile, "external content");
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new ScriptedRunner(
                ToolCall("files.read", JsonSerializer.Serialize(new { path = externalFile })),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner, withInProcessBroker: true);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "external directory approval");
            var runId = created.GetProperty("run_id").GetString()!;
            var workspaceId = created.GetProperty("workspace_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30)),
                "run never reached waiting_approval");
            var approvalId = await ReadLatestApprovalIdAsync(client, runId);
            Assert.NotNull(approvalId);

            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var approvalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { scope = "once", decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
            var approvalJson = await approvalResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("authorized", approvalJson.GetProperty("status").GetString());
            if (approvalJson.TryGetProperty("rule_saved", out var rs))
                Assert.Equal(JsonValueKind.False, rs.ValueKind);
            Assert.False(approvalJson.TryGetProperty("rule_id", out var rid) && !string.IsNullOrWhiteSpace(rid.GetString()));

            using var policyResponse = await client.GetAsync(
                $"/agent/v1/policies?scope=project&workspace_id={Uri.EscapeDataString(workspaceId)}");
            policyResponse.EnsureSuccessStatusCode();
            Assert.Equal(
                0,
                (await policyResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear();
        }
    }

    [Fact]
    public async Task Same_session_second_tool_reuses_broker_grant()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        var dataRoot = Path.GetDirectoryName(wsPath)!;
        var externalPath = Path.Combine(dataRoot, "external");
        Directory.CreateDirectory(externalPath);
        var externalFile = Path.Combine(externalPath, "note.txt");
        File.WriteAllText(externalFile, "external content");
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new ScriptedRunner(
                ToolCall("files.read", JsonSerializer.Serialize(new { path = externalFile })),
                ToolCall("files.list", JsonSerializer.Serialize(new { path = externalPath })),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner, withInProcessBroker: true);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "grant reuse");
            var runId = created.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30)),
                "run never reached waiting_approval");
            var approvalId = await ReadLatestApprovalIdAsync(client, runId);
            Assert.NotNull(approvalId);

            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var approvalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { scope = "once", decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);

            Assert.True(
                await WaitForStatusAsync(
                    client,
                    runId,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    TimeSpan.FromSeconds(60)),
                "run did not complete after grant");
            var events = await ReadEventsAsync(client, runId);
            Assert.Equal(1, events.Count(e => e.TryGetProperty("event_type", out var type) && type.GetString() == "approval.requested"));
            Assert.Contains(events, e => e.TryGetProperty("event_type", out var type) && type.GetString() == "tool.execution_completed");
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear();
        }
    }

    [Fact]
    public async Task New_and_forked_sessions_ask_again()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        var dataRoot = Path.GetDirectoryName(wsPath)!;
        var externalPath = Path.Combine(dataRoot, "external");
        Directory.CreateDirectory(externalPath);
        var externalFile = Path.Combine(externalPath, "note.txt");
        File.WriteAllText(externalFile, "external content");
        BackendWebApplicationFactory? factory = null;
        try
        {
            string externalArguments = JsonSerializer.Serialize(new { path = externalFile });
            var runner = new ScriptedRunner(
                ToolCall("files.read", externalArguments),
                FinalStep("done"),
                ToolCall("files.read", externalArguments),
                FinalStep("done"),
                ToolCall("files.read", externalArguments),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner, withInProcessBroker: true);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "first grant");
            var runId = created.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30)),
                "first run never reached waiting_approval");
            var approvalId = await ReadLatestApprovalIdAsync(client, runId);
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var approvalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { scope = "once", decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, TimeSpan.FromSeconds(60)),
                "first run did not finish before second session run");

            var otherSessionId = await CreateSessionAsync(client);
            var otherCreated = await CreateRunAndTrustAsync(client, otherSessionId, wsPath, "new session asks");
            var otherRunId = otherCreated.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(client, otherRunId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30)),
                "new session run did not ask again");
            var otherApprovalId = await ReadLatestApprovalIdAsync(client, otherRunId);
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var otherApprovalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{otherRunId}/approvals/{otherApprovalId}",
                new { scope = "once", decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, otherApprovalResponse.StatusCode);
            Assert.True(
                await WaitForStatusAsync(client, otherRunId, new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, TimeSpan.FromSeconds(60)),
                "new session run did not finish before forked session run");

            using var forkResp = await client.PostAsJsonAsync(
                $"/sessions/{sessionId}/fork",
                new { up_to_message_index = 999999 });
            forkResp.EnsureSuccessStatusCode();
            var forkedSessionId = (await forkResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session").GetProperty("id").GetString()!;
            Assert.NotEqual(sessionId, forkedSessionId);
            var forkedCreated = await CreateRunAndTrustAsync(client, forkedSessionId, wsPath, "forked session asks");
            var forkedRunId = forkedCreated.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(client, forkedRunId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30)),
                "forked session run did not ask again");
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear();
        }
    }

    [Fact]
    public async Task Deleting_session_revokes_its_broker_directory_grants()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        var dataRoot = Path.GetDirectoryName(wsPath)!;
        var externalPath = Path.Combine(dataRoot, "external");
        Directory.CreateDirectory(externalPath);
        var externalFile = Path.Combine(externalPath, "note.txt");
        File.WriteAllText(externalFile, "external content");
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new ScriptedRunner(
                ToolCall("files.read", JsonSerializer.Serialize(new { path = externalFile })),
                FinalStep("done"),
                ToolCall("files.read", JsonSerializer.Serialize(new { path = externalFile })),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner, withInProcessBroker: true);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "grant then delete");
            var runId = created.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30)),
                "run never reached waiting_approval");
            var approvalId = await ReadLatestApprovalIdAsync(client, runId);
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var approvalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { scope = "once", decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, TimeSpan.FromSeconds(60)),
                "granted run did not finish before delete");

            using var deleteResp = await client.DeleteAsync($"/sessions/{sessionId}");
            Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);
            Assert.Equal("ok", (await deleteResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

            var freshSessionId = await CreateSessionAsync(client);
            var freshCreated = await CreateRunAndTrustAsync(client, freshSessionId, wsPath, "after delete asks");
            var freshRunId = freshCreated.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(client, freshRunId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30)),
                "fresh session run did not ask after delete");
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear();
        }
    }

    [Theory]
    [InlineData("unknown-tool-xyz.exe", "terminal_unknown_executable")]
    [InlineData("rm.exe", "terminal_delete_requires_approval")]
    [InlineData("git", "terminal_external_mutation_requires_approval")]
    public async Task Terminal_reason_reaches_public_api(string executable, string expectedReason)
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var argumentsJson = executable == "git"
                ? JsonSerializer.Serialize(new { executable, arguments = new[] { "push" } })
                : JsonSerializer.Serialize(new { executable, arguments = new[] { "--version" } });
            var runner = new ScriptedRunner(
                ToolCall("terminal.run", argumentsJson),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner, withInProcessBroker: true);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "terminal reason");
            var runId = created.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30)),
                "terminal run never reached waiting_approval");
            var events = await ReadEventsAsync(client, runId);
            Assert.Contains(
                events,
                e => e.TryGetProperty("event_type", out var type)
                    && type.GetString() == "approval.requested"
                    && e.TryGetProperty("safe_summary", out var summary)
                    && summary.GetString() == expectedReason);
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear();
        }
    }

    [Fact]
    public async Task Approval_OnceScope_DoesNotPersistReusableRule()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var (fact, client, runId, approvalId) = await StartWaitingApprovalRunAsync(wsPath, dataPaths);
            factory = fact;
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();

            using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { scope = "once", decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("authorized", json.GetProperty("status").GetString());
            if (json.TryGetProperty("rule_saved", out var rs))
                Assert.Equal(JsonValueKind.False, rs.ValueKind);
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear();
        }
    }

    private static async Task<(BackendWebApplicationFactory Factory, HttpClient Client, string RunId, string ApprovalId)> StartWaitingApprovalRunAsync(string wsPath, RuBackend.Business.DataPaths dataPaths)
    {
        var runner = new ScriptedRunner(new AssistantStep(
            null,
            new[] { new ModelToolCall(null, "terminal.run", "1.0.0",
                JsonDocument.Parse("""{"executable":"unknown-tool-xyz.exe","arguments":["--version"]}""").RootElement.Clone()) },
            AssistantFinishReason.ToolCalls,
            0));
        var factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner, withInProcessBroker: true);
        var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = wsPath,
            prompt = "approval test",
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N"),
        };
        using var runResp = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        var created = await runResp.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("run_id").GetString()!;
        var trustPayload = new
        {
            trust_request_id = created.GetProperty("trust_request_id").GetString(),
            display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
            decision = "approve",
        };
        using var trustResp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/trust", trustPayload);
        trustResp.EnsureSuccessStatusCode();

        Assert.True(
            await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30)),
            "run never reached waiting_approval");

        var approvalId = await ReadLatestApprovalIdAsync(client, runId);
        Assert.NotNull(approvalId);
        return (factory, client, runId, approvalId!);
    }

    private static async Task<JsonElement> CreateRunAndTrustAsync(
        HttpClient client,
        string sessionId,
        string workspacePath,
        string prompt)
    {
        var runBody = new
        {
            session_id = sessionId,
            workspace_path = workspacePath,
            prompt,
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N"),
        };
        using var runResponse = await client.PostAsJsonAsync("/agent/v1/runs", runBody);
        runResponse.EnsureSuccessStatusCode();
        var created = await runResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("run_id").GetString()!;
        if (created.TryGetProperty("trust_request_id", out var trustId)
            && trustId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(trustId.GetString()))
        {
            using var trustResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/trust",
                new
                {
                    trust_request_id = trustId.GetString(),
                    display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
                    decision = "approve",
                });
            trustResponse.EnsureSuccessStatusCode();
        }

        return created;
    }

    private static async Task<string?> ReadLatestApprovalIdAsync(HttpClient client, string runId)
    {
        var events = await ReadEventsAsync(client, runId);
        string? latestApprovalId = null;
        foreach (var evt in events)
        {
            if (evt.TryGetProperty("event_type", out var eventType) && eventType.GetString() == "approval.requested")
            {
                if (evt.TryGetProperty("approval_id", out var aid))
                {
                    latestApprovalId = aid.GetString();
                }
            }
        }
        return latestApprovalId;
    }

    private static async Task<List<JsonElement>> ReadEventsAsync(HttpClient client, string runId, long? afterSequence = null)
    {
        var url = $"/agent/v1/runs/{runId}/events?live=0";
        if (afterSequence is { } after)
        {
            url += $"&after_sequence={after}";
        }

        using var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var events = new List<JsonElement>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data: ".Length..];
            if (payload.Contains("\"event_type\"", StringComparison.Ordinal))
            {
                events.Add(JsonDocument.Parse(payload).RootElement.Clone());
            }
        }

        return events;
    }

    private static async Task<bool> WaitForStatusAsync(HttpClient client, string runId, IReadOnlySet<string> states, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200).ConfigureAwait(false);
            var current = await GetStatusAsync(client, runId).ConfigureAwait(false);
            if (states.Contains(current))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string> GetStatusAsync(HttpClient client, string runId)
    {
        using var resp = await client.GetAsync($"/agent/v1/runs/{runId}");
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("status").GetString()!;
    }

    private sealed class ScriptedRunner : IModelStepRunner
    {
        private readonly Queue<AssistantStep> _steps = new();

        public ScriptedRunner(params AssistantStep[] steps)
        {
            foreach (var s in steps) _steps.Enqueue(s);
        }

        public Task<ModelStepResult> RunStepAsync(ModelStepRequest request, Func<AssistantDelta, ValueTask> publishDelta, CancellationToken cancellationToken)
        {
            var step = _steps.Count > 0 ? _steps.Dequeue() : FinalStep("script exhausted; done");
            return Task.FromResult(new ModelStepResult(step, SystemPromptReceipt.Empty));
        }
    }

    private static AssistantStep ToolCall(string toolId, string argumentsJson) =>
        new(
            null,
            new[]
            {
                new ModelToolCall(
                    null,
                    toolId,
                    "1.0.0",
                    JsonDocument.Parse(argumentsJson).RootElement.Clone()),
            },
            AssistantFinishReason.ToolCalls,
            0);

    private static AssistantStep FinalStep(string text) =>
        new(text, Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0);

    private static void SqliteConnectionClear()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
    }
}
