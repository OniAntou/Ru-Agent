using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Exercises the replayed activity feed against a real in-process Broker.
/// </summary>
/// <remarks>
/// This suite constructs <c>BackendWebApplicationFactory</c> with
/// <c>withInProcessBroker: true</c>, so it belongs to the heavy collection: it gates
/// on the same ports, process handles and CPU as the other heavy suites, and running
/// it beside them starved the wall-clock deadlines its replay assertions wait on. The
/// symptom was a replayed task list that arrived as zero rows in a full-assembly run
/// while passing on its own, which is why the collection rule exists.
/// </remarks>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class AgentRunActivitiesTests
{
    [Fact]
    public async Task Activities_reject_malformed_run_id()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        using var resp = await client.GetAsync("/agent/v1/runs/not-a-run-id/activities");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_run_id", body);
    }

    [Fact]
    public async Task Activities_return_not_found_for_unknown_run()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        using var resp = await client.GetAsync("/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/activities");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("run_not_found", body);
    }

    [Fact]
    public async Task Activities_return_empty_list_for_a_run_without_tool_calls()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(client, session, tmp.Path, "prompt without tools", "act-key-" + Guid.NewGuid().ToString("N"));
        await client.PostAsync($"/agent/v1/runs/{runId}/cancel", null);

        using var resp = await client.GetAsync($"/agent/v1/runs/{runId}/activities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("activities", out var activities));
        Assert.Equal(JsonValueKind.Array, activities.ValueKind);
        Assert.Equal(0, activities.GetArrayLength());
    }

    /// <summary>
    /// The replayed feed must carry the agent's task list, not just its tool rows.
    /// </summary>
    /// <remarks>
    /// This is what makes the checklist survive leaving a session. The transcript is
    /// rebuilt from this endpoint, so an activity kind the replay does not know about is
    /// simply gone the next time the session is opened -- which is exactly the failure a
    /// kind added later falls into, because nothing fails when a switch has no case for it.
    /// </remarks>
    [Fact]
    public async Task Replayed_activities_include_the_task_list()
    {
        var steps = new Queue<AssistantStep>();
        steps.Enqueue(new AssistantStep(
            null,
            new[]
            {
                new ModelToolCall(
                    null,
                    "todo_write",
                    "1.0.0",
                    JsonDocument.Parse("""
                        {"todos":[
                          {"content":"Read the failing test","status":"completed"},
                          {"content":"Fix the validator","status":"in_progress"}]}
                        """).RootElement),
            },
            AssistantFinishReason.ToolCalls,
            0));
        steps.Enqueue(new AssistantStep(
            "planned",
            Array.Empty<ModelToolCall>(),
            AssistantFinishReason.Stop,
            0));

        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(
            dataPaths: tmp.DataPaths,
            modelStepRunner: new ScriptedStepRunner(steps),
            withInProcessBroker: true);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(
            client, session, tmp.Path, "plan this", "todo-act-key-" + Guid.NewGuid().ToString("N"));
        await TrustAsync(client, runId);
        await WaitForTerminalAsync(client, runId);

        using var resp = await client.GetAsync($"/agent/v1/runs/{runId}/activities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var activities = json.GetProperty("activities").EnumerateArray().ToArray();

        // The tool row is replayed, as it always was. Which display label it carries is
        // not this test's subject, so it is asserted only as "some non-todo row exists":
        // pinning the label here would make a display change look like a replay failure.
        Assert.Contains(
            activities,
            activity => activity.GetProperty("kind").GetString() != "todo");

        // The task-list tool is its own kind now, so the replayed feed carries two rows of
        // that kind: the tool row and the row holding the list. Only one of them is the
        // list, and that is the one this asserts -- counting rows of a kind would tie the
        // test to how many activities happen to share it.
        var withList = SelectTaskLists(activities);
        Assert.True(withList.Length == 1, $"expected one replayed task list, saw {withList.Length}");

        var rows = withList[0].GetProperty("todos").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("Read the failing test", rows[0].GetProperty("content").GetString());
        Assert.Equal("completed", rows[0].GetProperty("status").GetString());
        Assert.Equal("in_progress", rows[1].GetProperty("status").GetString());
    }

    /// <summary>
    /// A reopened session must still show what the model was thinking: the
    /// reasoning rides a durable payload the replay joins back onto the
    /// thinking row, instead of living only in the transient live feed.
    /// </summary>
    [Fact]
    public async Task Replayed_thinking_carries_the_recorded_reasoning()
    {
        var steps = new Queue<AssistantStep>();
        steps.Enqueue(new AssistantStep(
            null,
            new[]
            {
                new ModelToolCall(
                    null,
                    "todo_write",
                    "1.0.0",
                    JsonDocument.Parse("""
                        {"todos":[
                          {"content":"Read the failing test","status":"completed"}]}
                        """).RootElement),
            },
            AssistantFinishReason.ToolCalls,
            0));
        steps.Enqueue(new AssistantStep(
            "planned",
            Array.Empty<ModelToolCall>(),
            AssistantFinishReason.Stop,
            0));
        var reasoning = new Queue<string?>();
        reasoning.Enqueue("planning the fix");
        reasoning.Enqueue("wrapping up");

        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(
            dataPaths: tmp.DataPaths,
            modelStepRunner: new ScriptedStepRunner(steps, reasoning),
            withInProcessBroker: true);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(
            client, session, tmp.Path, "plan this", "reason-act-key-" + Guid.NewGuid().ToString("N"));
        await TrustAsync(client, runId);
        await WaitForTerminalAsync(client, runId);

        using var resp = await client.GetAsync($"/agent/v1/runs/{runId}/activities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var activities = json.GetProperty("activities").EnumerateArray().ToArray();

        var thinking = activities
            .Where(activity => activity.GetProperty("kind").GetString() == "thinking")
            .ToArray();
        Assert.True(thinking.Length == 2, $"expected two replayed thinking rows, saw {thinking.Length}");
        var details = thinking
            .Select(activity => activity.TryGetProperty("detail", out var detail) ? detail.GetString() : null)
            .ToArray();
        Assert.Contains("planning the fix", details);
        Assert.Contains("wrapping up", details);
    }

    /// <summary>
    /// Approves the trust challenge a run raises for a workspace outside the project root.
    /// </summary>
    /// <remarks>
    /// The challenge values come from the run status rather than the create response, so
    /// this works whichever helper produced the run id.
    /// </remarks>
    private static async Task TrustAsync(HttpClient client, string runId)
    {
        using var statusResp = await client.GetAsync($"/agent/v1/runs/{runId}");
        var status = await statusResp.Content.ReadFromJsonAsync<JsonElement>();

        if (!status.TryGetProperty("trust_request_id", out var trustRequestId)
            || trustRequestId.ValueKind != JsonValueKind.String)
        {
            // Nothing to approve: the workspace was already trusted.
            return;
        }

        using var resp = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{runId}/trust",
            new
            {
                trust_request_id = trustRequestId.GetString(),
                display_identity_hash = status.GetProperty("display_identity_hash").GetString(),
                decision = "approve",
            });
        resp.EnsureSuccessStatusCode();
    }

    private static JsonElement[] SelectTaskLists(JsonElement[] activities) =>
        activities
            .Where(activity => activity.GetProperty("kind").GetString() == "todo")
            .Where(activity => activity.TryGetProperty("todos", out var value)
                && value.ValueKind == JsonValueKind.Array)
            .ToArray();

    private static async Task WaitForTerminalAsync(HttpClient client, string runId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            using var response = await client.GetAsync($"/agent/v1/runs/{runId}");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var status = document.RootElement.GetProperty("status").GetString();
            if (status is "completed" or "failed")
            {
                Assert.True(status == "completed", $"run ended as {status}");
                return;
            }
        }

        Assert.Fail("run did not reach a terminal status in time");
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private static async Task<string> CreateRunAsync(HttpClient client, string sessionId, string workspacePath, string prompt, string key)
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
        if (resp.StatusCode != HttpStatusCode.Created)
        {
            var txt = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Create run failed {resp.StatusCode} {txt}");
        }

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("run_id").GetString()!;
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
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ru-activities-test-" + Guid.NewGuid().ToString("N"));
            var ws = System.IO.Path.Combine(root, "ws");
            Directory.CreateDirectory(ws);
            var dataRoot = System.IO.Path.Combine(root, "data");
            Directory.CreateDirectory(dataRoot);
            var resolved = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, System.IO.Path.Combine(root, "logs"));
            return new TempWorkspace(root, ws, resolved);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        }
    }

    private sealed class ScriptedStepRunner : IModelStepRunner
    {
        private readonly Queue<AssistantStep> _steps;
        private readonly Queue<string?> _reasoning;

        public ScriptedStepRunner(Queue<AssistantStep> steps, Queue<string?>? reasoning = null)
        {
            _steps = steps;
            _reasoning = reasoning ?? new Queue<string?>();
        }

        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("no scripted step left");
            }

            // The coordinator validates the returned receipt against the prompt context it
            // built, so an empty receipt fails as prompt_contract_invalid.
            var receipt = SystemPromptComposer.Compose(request.PromptContext).Receipt;
            var reasoning = _reasoning.Count > 0 ? _reasoning.Dequeue() : null;
            return Task.FromResult(new ModelStepResult(_steps.Dequeue(), receipt, usage: null, reasoningText: reasoning));
        }
    }
}
