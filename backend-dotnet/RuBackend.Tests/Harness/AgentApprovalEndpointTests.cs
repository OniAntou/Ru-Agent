using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Harness.Policy;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Approval semantics over the real host runtime with a scripted model. Each case
/// drives the run to a parked state, so it waits on process spawning and the shared
/// workspace; running it beside the other terminal-spawning suites starves those
/// deadlines, so it shares the non-parallel process-heavy collection.
///
/// A waiting run is observed through its durable event stream, not the projected
/// run status: the status can settle to a terminal answer while the outbox still
/// owes the client the run's one durable row, so a status poll can call a run
/// finished when its approval event is already committed and readable.
/// </summary>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class AgentApprovalEndpointTests
{
    /// <summary>
    /// How long a run is given to reach an expected state.
    ///
    /// A guard against a hang, not a performance budget. Several of these tests drive
    /// three runs in sequence through a real loopback host while the rest of the suite
    /// runs in parallel, and at thirty seconds the last one in the chain expired often
    /// enough to fail as "did not ask again" — a message that points at the approval
    /// logic rather than at the clock.
    /// </summary>
    private static readonly TimeSpan StateDeadline = TimeSpan.FromSeconds(90);

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
        SqliteConnectionClear(dataPaths);
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
        SqliteConnectionClear(dataPaths);
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
        SqliteConnectionClear(dataPaths);
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
            SqliteConnectionClear(dataPaths);
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
            SqliteConnectionClear(dataPaths);
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
            SqliteConnectionClear(dataPaths);
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
            SqliteConnectionClear(dataPaths);
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
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "external directory approval");
            var runId = created.GetProperty("run_id").GetString()!;
            var workspaceId = created.GetProperty("workspace_id").GetString()!;
            var approvalId = await WaitForApprovalIdAsync(client, runId, StateDeadline);
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
            SqliteConnectionClear(dataPaths);
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
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "grant reuse");
            var runId = created.GetProperty("run_id").GetString()!;
            var approvalId = await WaitForApprovalIdAsync(client, runId, StateDeadline);
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
                    StateDeadline),
                "run did not complete after grant");
            var events = await ReadEventsAsync(client, runId);
            Assert.Equal(1, events.Count(e => e.TryGetProperty("event_type", out var type) && type.GetString() == "approval.requested"));
            Assert.Contains(events, e => e.TryGetProperty("event_type", out var type) && type.GetString() == "tool.execution_completed");
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task New_sessions_ask_again()
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
            // The model emits one tool call per run, and the broker only approves a
            // call it has already proposed. Polling the durable stream therefore
            // also proves the right leg asked: a grant from an earlier leg can
            // never produce an approval event on the new run's stream.
            string externalArguments = JsonSerializer.Serialize(new { path = externalFile });
            var runner = new ScriptedRunner(
                ToolCall("files.read", externalArguments),
                FinalStep("done"),
                ToolCall("files.read", externalArguments),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "first grant");
            var runId = created.GetProperty("run_id").GetString()!;
            var approvalId = await WaitForApprovalIdAsync(client, runId, StateDeadline);
            Assert.NotNull(approvalId);
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var approvalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { scope = "once", decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, StateDeadline),
                "first run did not finish before second session run");

            var otherSessionId = await CreateSessionAsync(client);
            var otherCreated = await CreateRunAndTrustAsync(client, otherSessionId, wsPath, "new session asks");
            var otherRunId = otherCreated.GetProperty("run_id").GetString()!;
            var otherApprovalId = await WaitForApprovalIdAsync(client, otherRunId, StateDeadline);
            Assert.NotNull(otherApprovalId);
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var otherApprovalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{otherRunId}/approvals/{otherApprovalId}",
                new { scope = "once", decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, otherApprovalResponse.StatusCode);
            Assert.True(
                await WaitForStatusAsync(client, otherRunId, new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, StateDeadline),
                "new session run did not finish");
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
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
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "grant then delete");
            var runId = created.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, StateDeadline),
                "run never reached waiting_approval");
            var approvalId = await ReadLatestApprovalIdAsync(client, runId);
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var approvalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { scope = "once", decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, StateDeadline),
                "granted run did not finish before delete");

            using var deleteResp = await client.DeleteAsync($"/sessions/{sessionId}");
            Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);
            Assert.Equal("ok", (await deleteResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

            var freshSessionId = await CreateSessionAsync(client);
            var freshCreated = await CreateRunAndTrustAsync(client, freshSessionId, wsPath, "after delete asks");
            var freshRunId = freshCreated.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(client, freshRunId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, StateDeadline),
                "fresh session run did not ask after delete");
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Theory]
    [InlineData("unknown-tool-xyz.exe", "terminal_external_cwd_requires_approval")]
    [InlineData("rm.exe", "terminal_external_cwd_requires_approval")]
    [InlineData("git", "terminal_external_cwd_requires_approval")]
    public async Task Terminal_reason_reaches_public_api(string executable, string expectedReason)
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var argumentsJson = executable switch
            {
                "git" => JsonSerializer.Serialize(new { executable, arguments = new[] { "push" } }),
                "unknown-tool-xyz.exe" => JsonSerializer.Serialize(new { executable, arguments = new[] { "--query-approval-state" } }),
                _ => JsonSerializer.Serialize(new { executable, arguments = new[] { "--version" } }),
            };
            var runner = new ScriptedRunner(
                ToolCall("terminal.run", argumentsJson),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "terminal reason");
            var runId = created.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, StateDeadline),
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
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task Batch_approval_persists_all_calls_and_resumes_without_reproposal()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        File.WriteAllText(Path.Combine(wsPath, "README.md"), "batch");
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new ScriptedRunner(
                BatchToolStep(),
                FinalStep("done"),
                ToolCall("files.read", """{"path":"README.md"}"""),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(
                dataPaths: dataPaths,
                modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(
                client,
                sessionId,
                wsPath,
                "batch approval");
            var runId = created.GetProperty("run_id").GetString()!;
            var approvalId = await WaitForApprovalIdAsync(client, runId, StateDeadline);
            Assert.NotNull(approvalId);

            var before = await ReadEventsAsync(client, runId);
            var proposedBefore = before
                .Where(item => item.GetProperty("event_type").GetString() == "tool.proposed")
                .ToArray();
            Assert.Equal(3, proposedBefore.Length);
            Assert.Equal(
                new[] { "1", "2", "3" },
                proposedBefore.Select(item => item.GetProperty("safe_summary").GetString()).ToArray());
            Assert.DoesNotContain(
                before,
                item => item.GetProperty("event_type").GetString() == "tool.execution_requested");
            Assert.DoesNotContain(
                before,
                item => item.GetProperty("event_type").GetString() == "tool.execution_completed");
            var requestedBefore = before
                .Where(item => item.GetProperty("event_type").GetString() == "approval.requested")
                .ToArray();
            Assert.Single(requestedBefore);
            Assert.Equal(
                "terminal_external_cwd_requires_approval",
                requestedBefore[0].GetProperty("safe_summary").GetString());

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
                    StateDeadline));

            var after = await ReadEventsAsync(client, runId);
            Assert.Equal(
                proposedBefore.Length + 1,
                after.Count(item => item.GetProperty("event_type").GetString() == "tool.proposed"));
            Assert.Equal(
                4,
                after.Count(item => item.GetProperty("event_type").GetString() == "tool.execution_completed"));
            Assert.Equal(
                1,
                after.Count(item => item.GetProperty("event_type").GetString() == "approval.requested"));
            // Batch request, the prose answer after the failed unknown-executable
            // execution (answered with a recovery note), the corrected read, then
            // the script's final answer after that verified call: the failed tool
            // no longer ends the run and the unverified prose is never accepted.
            Assert.Contains(after, item => item.GetProperty("event_type").GetString() == "run.note_added");
            Assert.Equal(4, runner.RequestCount);
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
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
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task Approval_AllowAlways_SavesProjectRule_AndSecondRunSkipsApproval()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new ScriptedRunner(
                ToolCall("terminal.run", """{"executable":"unknown-tool-xyz.exe","arguments":["--query-approval-state"]}"""),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "remember me");
            var runId = created.GetProperty("run_id").GetString()!;
            var workspaceId = created.GetProperty("workspace_id").GetString()!;
            var approvalId = await WaitForApprovalIdAsync(client, runId, StateDeadline);
            Assert.NotNull(approvalId);

            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { command = "allow_always" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("authorized", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("rule_saved").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("rule_id").GetString()));
            Assert.Equal("project", json.GetProperty("matcher_preview").GetProperty("scope").GetString());

            using var policies = await client.GetAsync($"/agent/v1/policies?scope=project&workspace_id={Uri.EscapeDataString(workspaceId)}");
            policies.EnsureSuccessStatusCode();
            Assert.Equal(1, (await policies.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());

            // Let the first run release the workspace before starting the second.
            Assert.True(
                await WaitForStatusAsync(
                    client,
                    runId,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    StateDeadline),
                "first run did not finish after allow_always");

            // The identical call on a second run must not ask again.
            var created2 = await CreateRunAndTrustAsync(client, sessionId, wsPath, "remember me");
            var runId2 = created2.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(
                    client,
                    runId2,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    StateDeadline),
                "second run did not finish");
            var events2 = await ReadEventsAsync(client, runId2);
            Assert.DoesNotContain(events2, e => e.TryGetProperty("event_type", out var type) && type.GetString() == "approval.requested");
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task Allow_once_remembers_intent_for_the_rest_of_the_run()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new ScriptedRunner(
                TwoSameTerminalCalls(),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "remember in run");
            var runId = created.GetProperty("run_id").GetString()!;
            var approvalId = await WaitForApprovalIdAsync(client, runId, StateDeadline);
            Assert.NotNull(approvalId);

            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var approvalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { command = "allow_once" });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);

            Assert.True(
                await WaitForStatusAsync(
                    client,
                    runId,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    StateDeadline),
                "second identical call asked again instead of reusing the session allow");
            var events = await ReadEventsAsync(client, runId);
            Assert.Equal(1, events.Count(e => e.TryGetProperty("event_type", out var type) && type.GetString() == "approval.requested"));
            Assert.Equal(2, events.Count(e => e.TryGetProperty("event_type", out var type) && type.GetString() == "tool.execution_completed"));
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task Allow_once_remembers_intent_for_a_later_run_in_the_same_session()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new ScriptedRunner(
                ToolCall("terminal.run", Command("session-memory")),
                FinalStep("done"),
                ToolCall("terminal.run", Command("session-memory")),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "remember across runs");
            var runId = created.GetProperty("run_id").GetString()!;
            var approvalId = await WaitForApprovalIdAsync(client, runId, StateDeadline);
            Assert.NotNull(approvalId);

            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var approvalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { command = "allow_once" });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
            Assert.True(
                await WaitForStatusAsync(
                    client,
                    runId,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    StateDeadline),
                "first run did not finish after allow_once");

            var created2 = await CreateRunAndTrustAsync(client, sessionId, wsPath, "remember across runs");
            var runId2 = created2.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(
                    client,
                    runId2,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    StateDeadline),
                "later run asked again instead of reusing the session allow");
            var events2 = await ReadEventsAsync(client, runId2);
            Assert.DoesNotContain(events2, e => e.TryGetProperty("event_type", out var type) && type.GetString() == "approval.requested");
            Assert.Equal(1, events2.Count(e => e.TryGetProperty("event_type", out var type) && type.GetString() == "tool.execution_completed"));
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task Different_intent_in_the_same_session_asks_again()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new ScriptedRunner(
                ToolCall("terminal.run", Command("first")),
                FinalStep("done"),
                ToolCall("terminal.run", Command("second")),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "different intent");
            var runId = created.GetProperty("run_id").GetString()!;
            var approvalId = await WaitForApprovalIdAsync(client, runId, StateDeadline);
            Assert.NotNull(approvalId);

            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var approvalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { command = "allow_once" });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
            Assert.True(
                await WaitForStatusAsync(
                    client,
                    runId,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    StateDeadline),
                "first run did not finish after allow_once");

            var created2 = await CreateRunAndTrustAsync(client, sessionId, wsPath, "different intent");
            var runId2 = created2.GetProperty("run_id").GetString()!;
            var approvalId2 = await WaitForApprovalIdAsync(client, runId2, StateDeadline);
            Assert.NotNull(approvalId2);
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task New_session_asks_again_for_the_same_intent()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new ScriptedRunner(
                ToolCall("terminal.run", Command("fresh")),
                FinalStep("done"),
                ToolCall("terminal.run", Command("fresh")),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "first session");
            var runId = created.GetProperty("run_id").GetString()!;
            var approvalId = await WaitForApprovalIdAsync(client, runId, StateDeadline);
            Assert.NotNull(approvalId);

            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var approvalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { command = "allow_once" });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
            Assert.True(
                await WaitForStatusAsync(
                    client,
                    runId,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    StateDeadline),
                "first session run did not finish after allow_once");

            var otherSessionId = await CreateSessionAsync(client);
            var otherCreated = await CreateRunAndTrustAsync(client, otherSessionId, wsPath, "second session");
            var otherRunId = otherCreated.GetProperty("run_id").GetString()!;
            var otherApprovalId = await WaitForApprovalIdAsync(client, otherRunId, StateDeadline);
            Assert.NotNull(otherApprovalId);
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task External_directory_allow_does_not_write_the_session_ledger()
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
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "external approval");
            var runId = created.GetProperty("run_id").GetString()!;
            var approvalId = await WaitForApprovalIdAsync(client, runId, StateDeadline);
            Assert.NotNull(approvalId);

            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var approvalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { command = "allow_once" });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
            Assert.True(
                await WaitForStatusAsync(
                    client,
                    runId,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    StateDeadline),
                "external-directory run did not finish after allow_once");

            Assert.Empty(SessionApprovalLedger.Entries(sessionId, DateTimeOffset.UtcNow));
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task External_allow_always_saves_the_folder_pattern_and_a_new_session_skips_approval()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        var dataRoot = Path.GetDirectoryName(wsPath)!;
        var externalPath = Path.Combine(dataRoot, "external-always");
        Directory.CreateDirectory(externalPath);
        var firstFile = Path.Combine(externalPath, "note.txt");
        var secondFile = Path.Combine(externalPath, "later-note.txt");
        File.WriteAllText(firstFile, "external content");
        File.WriteAllText(secondFile, "later content");
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new ScriptedRunner(
                ToolCall("files.read", JsonSerializer.Serialize(new { path = firstFile })),
                FinalStep("done"),
                ToolCall("files.read", JsonSerializer.Serialize(new { path = secondFile })),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "external always");
            var runId = created.GetProperty("run_id").GetString()!;
            var workspaceId = created.GetProperty("workspace_id").GetString()!;
            var approvalId = await WaitForApprovalIdAsync(client, runId, StateDeadline);
            Assert.NotNull(approvalId);

            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();
            using var approvalResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { command = "allow_always" });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
            var approvalJson = await approvalResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("authorized", approvalJson.GetProperty("status").GetString());
            Assert.True(approvalJson.GetProperty("rule_saved").GetBoolean());
            Assert.Equal("project", approvalJson.GetProperty("matcher_preview").GetProperty("scope").GetString());
            var pattern = approvalJson.GetProperty("matcher_preview").GetProperty("canonical_path").GetString();
            Assert.NotNull(pattern);
            Assert.EndsWith("\\**", pattern, StringComparison.Ordinal);

            // The folder grant is durable: one project rule for the workspace.
            using (var policies = await client.GetAsync($"/agent/v1/policies?scope=project&workspace_id={Uri.EscapeDataString(workspaceId)}"))
            {
                policies.EnsureSuccessStatusCode();
                Assert.Equal(1, (await policies.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());
            }

            // The grant lives in the durable rule, not the in-process ledger.
            Assert.Empty(SessionApprovalLedger.Entries(sessionId, DateTimeOffset.UtcNow));

            Assert.True(
                await WaitForStatusAsync(
                    client,
                    runId,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    StateDeadline),
                "external allow_always run did not finish");

            // A different file in the same folder must not ask again, even in a
            // new session: the remembered folder survives the session boundary.
            var otherSessionId = await CreateSessionAsync(client);
            var created2 = await CreateRunAndTrustAsync(client, otherSessionId, wsPath, "external always");
            var runId2 = created2.GetProperty("run_id").GetString()!;
            Assert.True(
                await WaitForStatusAsync(
                    client,
                    runId2,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    StateDeadline),
                "second run did not finish");
            var events2 = await ReadEventsAsync(client, runId2);
            Assert.DoesNotContain(events2, e => e.TryGetProperty("event_type", out var type) && type.GetString() == "approval.requested");
            Assert.Equal(1, events2.Count(e => e.TryGetProperty("event_type", out var type) && type.GetString() == "tool.execution_completed"));
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    private static async Task<(BackendWebApplicationFactory Factory, HttpClient Client, string RunId, string ApprovalId)> StartWaitingApprovalRunAsync(string wsPath, RuBackend.Business.DataPaths dataPaths)
    {
        var runner = new ScriptedRunner(ToolCall("terminal.run", """{"executable":"unknown-tool-xyz.exe","arguments":["--query-approval-state"]}"""));
        var factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
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

        var approvalId = await WaitForApprovalIdAsync(client, runId, StateDeadline);
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
        return LatestApprovalId(events);
    }

    private static string? LatestApprovalId(IReadOnlyList<JsonElement> events)
    {
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

    private static async Task<string?> WaitForApprovalIdAsync(HttpClient client, string runId, TimeSpan timeout)
    {
        // The run status settles only after the outbox delivers the matching history
        // row, while the approval event commits earlier in the same step. Polling the
        // durable stream therefore sees the parked run while a status poll can still
        // read the previous projection and leave the approval id unread.
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var events = await ReadEventsAsync(client, runId).ConfigureAwait(false);
            if (LatestApprovalId(events) is { } approvalId)
            {
                return approvalId;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        return null;
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

        public int RequestCount { get; private set; }

        public ScriptedRunner(params AssistantStep[] steps)
        {
            foreach (var s in steps) _steps.Enqueue(s);
        }

        public Task<ModelStepResult> RunStepAsync(ModelStepRequest request, Func<AssistantDelta, ValueTask> publishDelta, CancellationToken cancellationToken)
        {
            RequestCount++;
            var step = _steps.Count > 0 ? _steps.Dequeue() : FinalStep("script exhausted; done");
            return Task.FromResult(new ModelStepResult(
                step,
                RuAgent.Harness.Core.Prompting.SystemPromptComposer
                    .Compose(request.PromptContext, nativeToolCalling: true)
                    .Receipt));
        }
    }

    private static AssistantStep BatchToolStep() =>
        new(
            null,
            new[]
            {
                new ModelToolCall(
                    "provider-batch-1",
                    "read",
                    "1.0.0",
                    JsonDocument.Parse("{\"path\":\"README.md\"}").RootElement.Clone()),
                new ModelToolCall(
                    "provider-batch-2",
                    "bash",
                    "1.0.0",
                    JsonDocument.Parse(JsonSerializer.Serialize(new { mode = "run", executable = "unknown-tool-batch.exe", arguments = new[] { "--query-approval-state" }, working_directory = Path.GetTempPath() }))
                        .RootElement.Clone()),
                new ModelToolCall(
                    "provider-batch-3",
                    "read",
                    "1.0.0",
                    JsonDocument.Parse("{\"path\":\"README.md\"}").RootElement.Clone()),
            },
            AssistantFinishReason.ToolCalls,
            0);

    private static AssistantStep TwoSameTerminalCalls()
    {
        var first = JsonDocument.Parse(Command("batched")).RootElement.Clone();
        var second = JsonDocument.Parse(Command("batched")).RootElement.Clone();
        return new(
            null,
            new[]
            {
                new ModelToolCall("provider-batch-1", "bash", "1.0.0", first),
                new ModelToolCall("provider-batch-2", "bash", "1.0.0", second),
            },
            AssistantFinishReason.ToolCalls,
            0);
    }

    private static string Command(string marker) =>
        JsonSerializer.Serialize(new { mode = "run", executable = "cmd.exe", arguments = new[] { "/c", "echo", marker }, working_directory = Path.GetTempPath() });

    private static AssistantStep ToolCall(string toolId, string argumentsJson)
    {
        var (mappedId, mappedJson) = MapToPi(toolId, argumentsJson);
        return new(
            null,
            new[]
            {
                new ModelToolCall(
                    "provider_call_1",
                    mappedId,
                    "1.0.0",
                    JsonDocument.Parse(mappedJson).RootElement.Clone()),
            },
            AssistantFinishReason.ToolCalls,
            0);
    }

    private static (string Id, string Json) MapToPi(string toolId, string argumentsJson)
    {
        var node = JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        var id = toolId;
        switch (toolId)
        {
            case "files.list": id = "list"; node.Remove("op"); break;
            case "files.glob": id = "glob"; node.Remove("op"); break;
            case "files.search": id = "grep"; node.Remove("op"); break;
            case "files.read": id = "read"; node.Remove("op"); break;
            case "files.write": id = "write"; node["op"] = "write"; break;
            case "files.edit": id = "edit"; break;
            case "files.delete": id = "write"; node["op"] = "delete"; break;
            case "files.restore": id = "write"; node["op"] = "restore"; break;
            case "terminal.run": id = "bash"; node["mode"] = "run"; node["working_directory"] ??= Path.GetTempPath(); break;
            case "terminal.shell": id = "bash"; node["mode"] = "shell"; break;
            case "web.search": id = "web_search"; node.Remove("op"); break;
            case "web.fetch": id = "web_fetch"; node.Remove("op"); break;
            case "help.docs": id = "help"; node.Remove("op"); break;
        }
        return (id, node.ToJsonString());
    }

    private static AssistantStep FinalStep(string text) =>
        new(text, Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0);

    private static void SqliteConnectionClear(RuBackend.Business.DataPaths dataPaths)
    {
        try { SqliteTestPools.ClearHarnessDatabase(dataPaths.AgentHarnessDatabase); } catch { }
    }
}
