using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Phase 1 remediation regression tests: snake_case run states on the API surface and
/// real trust challenges returned to clients instead of client-side fabricated values.
/// </summary>
public sealed class Phase1RemediationTests
{
    [Fact]
    public async Task Create_run_on_untrusted_workspace_returns_real_trust_challenge()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);

        var createJson = await CreateRunAsync(client, sessionId, tmp.Path, "prompt", "key-" + Guid.NewGuid().ToString("N"));

        Assert.Equal("waiting_workspace_trust", createJson.GetProperty("status").GetString());
        var trustRequestId = createJson.GetProperty("trust_request_id").GetString();
        var displayHash = createJson.GetProperty("display_identity_hash").GetString();
        Assert.False(string.IsNullOrWhiteSpace(trustRequestId));
        Assert.Matches(new Regex("^trust_[0-9a-f]{32}$"), trustRequestId);
        Assert.False(string.IsNullOrWhiteSpace(displayHash));
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), displayHash);
    }

    [Fact]
    public async Task Run_status_uses_snake_case_state_names()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var runId = (await CreateRunAsync(client, sessionId, tmp.Path, "prompt", "key-" + Guid.NewGuid().ToString("N")))
            .GetProperty("run_id").GetString()!;

        using var resp = await client.GetAsync($"/agent/v1/runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("waiting_workspace_trust", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Trust_approval_with_returned_challenge_moves_run_to_waiting_history()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var created = await CreateRunAsync(client, sessionId, tmp.Path, "prompt", "key-" + Guid.NewGuid().ToString("N"));
        var runId = created.GetProperty("run_id").GetString()!;

        var trustPayload = new
        {
            trust_request_id = created.GetProperty("trust_request_id").GetString(),
            display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
            decision = "approve",
        };
        using var resp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/trust", trustPayload);
        var trustBody = await resp.Content.ReadAsStringAsync();
        Assert.True(
            resp.StatusCode == HttpStatusCode.OK,
            $"trust returned {(int)resp.StatusCode}: {trustBody}");
        var json = JsonDocument.Parse(trustBody).RootElement;
        Assert.Equal("waiting_history", json.GetProperty("status").GetString());

        using var getResp = await client.GetAsync($"/agent/v1/runs/{runId}");
        var getJson = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("user_before_first_step", getJson.GetProperty("history_wait_kind").GetString());
    }

    [Fact]
    public async Task Project_root_workspace_auto_trusts_and_allows_full_access_without_prior_selection()
    {
        using var tmp = TempWorkspace.Create();
        var prevEnv = Environment.GetEnvironmentVariable("RU_PROJECT_ROOT");
        Environment.SetEnvironmentVariable("RU_PROJECT_ROOT", tmp.Path);
        try
        {
            using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
            using var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);

            var runPayload = new
            {
                session_id = sessionId,
                workspace_path = tmp.Path,
                prompt = "hello",
                idempotency_key = "key-" + Guid.NewGuid().ToString("N"),
                preset = "full_access"
            };

            using var resp = await client.PostAsJsonAsync("/agent/v1/runs", runPayload);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode, $"Expected success, got {resp.StatusCode}: {body}");

            var json = JsonDocument.Parse(body).RootElement;
            Assert.Equal("waiting_history_user", json.GetProperty("status").GetString());
            Assert.Equal("full_access", json.GetProperty("preset").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RU_PROJECT_ROOT", prevEnv);
        }
    }

    [Fact]
    public async Task Runtime_loop_delivers_prompt_and_drives_run_to_completed()
    {
        var steps = new Queue<RuAgent.Harness.Core.AssistantStep>();
        steps.Enqueue(new RuAgent.Harness.Core.AssistantStep(
            null,
            new[]
            {
                new RuAgent.Harness.Core.ModelToolCall(
                    null,
                    "files.list",
                    "1.0.0",
                    JsonDocument.Parse("{\"path\":\".\"}").RootElement),
            },
            RuAgent.Harness.Core.AssistantFinishReason.ToolCalls,
            0));
        steps.Enqueue(new RuAgent.Harness.Core.AssistantStep(
            "runtime loop done",
            Array.Empty<RuAgent.Harness.Core.ModelToolCall>(),
            RuAgent.Harness.Core.AssistantFinishReason.Stop,
            0));
        var fakeRunner = new ScriptedStepRunner(steps);

        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(
            dataPaths: tmp.DataPaths,
            modelStepRunner: fakeRunner,
            withInProcessBroker: true);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var created = await CreateRunAsync(client, sessionId, tmp.Path, "drive me", "key-" + Guid.NewGuid().ToString("N"));
        var runId = created.GetProperty("run_id").GetString()!;

        var trustPayload = new
        {
            trust_request_id = created.GetProperty("trust_request_id").GetString(),
            display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
            decision = "approve",
        };
        using var trustResp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/trust", trustPayload);
        trustResp.EnsureSuccessStatusCode();

        string? status = null;
        string lastBody = string.Empty;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            using var getResp = await client.GetAsync($"/agent/v1/runs/{runId}");
            lastBody = await getResp.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(lastBody);
                status = doc.RootElement.GetProperty("status").GetString();
            }
            catch (JsonException)
            {
                status = "unparseable:" + lastBody[..Math.Min(120, lastBody.Length)];
            }

            if (status is "completed" or "failed")
            {
                break;
            }
        }

        Assert.True(
            status == "completed",
            $"final status={status} last_body={lastBody[..Math.Min(300, lastBody.Length)]}");

        using var eventsResp = await client.GetAsync($"/agent/v1/runs/{runId}/events");
        var eventsBody = await eventsResp.Content.ReadAsStringAsync();
        Assert.True(
            eventsResp.IsSuccessStatusCode,
            $"events returned {(int)eventsResp.StatusCode}: {eventsBody[..Math.Min(200, eventsBody.Length)]}");
        var kinds = eventsBody
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line["data: ".Length..]).RootElement)
            .Where(root => root.TryGetProperty("event_type", out _))
            .Select(root => root.GetProperty("event_type").GetString())
            .ToArray();
        Assert.Contains("tool.execution_completed", kinds);
        Assert.Equal(2, kinds.Count(kind => kind == "model.step_completed"));
        Assert.Contains("event: final_snapshot", eventsBody, StringComparison.Ordinal);
    }

    private sealed class ScriptedStepRunner : RuAgent.Harness.Core.IModelStepRunner
    {
        private readonly Queue<RuAgent.Harness.Core.AssistantStep> _steps;
        public ScriptedStepRunner(Queue<RuAgent.Harness.Core.AssistantStep> steps) => _steps = steps;

        public Task<ModelStepResult> RunStepAsync(
            RuAgent.Harness.Core.ModelStepRequest request,
            Func<RuAgent.Harness.Core.AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("no scripted step left");
            }

            return Task.FromResult(new ModelStepResult(_steps.Dequeue(), SystemPromptReceipt.Empty));
        }
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private static async Task<JsonElement> CreateRunAsync(
        HttpClient client,
        string sessionId,
        string workspacePath,
        string prompt,
        string key)
    {
        Directory.CreateDirectory(workspacePath);
        var body = new
        {
            session_id = sessionId,
            workspace_path = workspacePath,
            prompt,
            preset = "standard",
            idempotency_key = key,
        };

        using var resp = await client.PostAsJsonAsync("/agent/v1/runs", body);
        if (resp.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            var txt = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Create run failed {resp.StatusCode} {txt}");
        }

        return await resp.Content.ReadFromJsonAsync<JsonElement>();
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
            var dataPaths = new RuBackend.Business.DataPaths(
                System.IO.Path.Combine(dataRoot, "memory.db"),
                System.IO.Path.Combine(dataRoot, "memory_texts.json"),
                System.IO.Path.Combine(dataRoot, "settings.json"),
                System.IO.Path.Combine(dataRoot, "history.json"),
                System.IO.Path.Combine(dataRoot, "sessions"),
                System.IO.Path.Combine(dataRoot, "activity.jsonl"));
            return new TempWorkspace(root, ws, dataPaths);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
