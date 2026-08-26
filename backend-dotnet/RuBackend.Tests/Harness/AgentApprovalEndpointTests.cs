using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RuAgent.Harness.Core;
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
    public async Task Approval_ExactScopePreview_StripsGlob()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var (fact, client, runId, approvalId) = await StartWaitingApprovalRunAsync(wsPath, dataPaths);
            factory = fact;
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();

            using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { scope = "run", decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.TryGetProperty("matcher_preview", out var preview), $"missing matcher_preview in {json.GetRawText()}");
            var previewText = preview.GetRawText();
            Assert.DoesNotContain("*", previewText);
            Assert.True(json.TryGetProperty("rule_saved", out var ruleSaved), $"missing rule_saved in {json.GetRawText()}");
            Assert.True(ruleSaved.ValueKind == JsonValueKind.True, $"Expected rule_saved true but got {ruleSaved} json:{json.GetRawText()}");
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
            Assert.False(json.TryGetProperty("rule_saved", out var rs) && rs.GetBoolean());

            // Denial resumes the run; a stale reuse of the same ID is rejected.
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
    public async Task Approval_ProjectScope_RuleSaved()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var (fact, client, runId, approvalId) = await StartWaitingApprovalRunAsync(wsPath, dataPaths);
            factory = fact;
            RuBackend.Harness.AgentV1Service.ClearHostApprovalsForTests();

            using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/approvals/{approvalId}", new { scope = "project", decision = "allow" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("authorized", json.GetProperty("status").GetString());
            Assert.True(json.TryGetProperty("rule_saved", out var rs2) && rs2.ValueKind == JsonValueKind.True, $"Expected rule_saved true but json: {json.GetRawText()}");
            Assert.True(json.TryGetProperty("rule_id", out var rid) && !string.IsNullOrWhiteSpace(rid.GetString()));
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
                JsonDocument.Parse("""{"executable":"git","arguments":["commit","-m","x"]}""").RootElement.Clone()) },
            AssistantFinishReason.ToolCalls,
            0));
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

        Assert.True(
            await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30)),
            "run never reached waiting_approval");

        var approvalId = await ReadLatestApprovalIdAsync(client, runId);
        Assert.NotNull(approvalId);
        return (factory, client, runId, approvalId!);
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

        public Task<AssistantStep> RunStepAsync(ModelStepRequest request, Func<AssistantDelta, ValueTask> publishDelta, CancellationToken cancellationToken)
        {
            var step = _steps.Count > 0 ? _steps.Dequeue() : FinalStep("script exhausted; done");
            return Task.FromResult(step);
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
