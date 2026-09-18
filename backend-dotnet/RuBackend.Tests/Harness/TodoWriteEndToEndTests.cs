using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The todo_write tool, driven end to end through the real stack.
/// </summary>
/// <remarks>
/// The unit tests cover the validator, the store and the executor. This one goes through
/// the Host, an in-process Broker and the runtime loop with a scripted model, which is the
/// only place the whole chain is exercised: the tool is advertised, the call reaches the
/// execution authority, the result comes back, and the task list is published as an
/// activity the client can see.
///
/// A live model is not needed and not used: the scripted runner returns the tool call the
/// way a provider would.
/// </remarks>
public sealed class TodoWriteEndToEndTests
{
    [Fact]
    public async Task A_scripted_todo_call_reaches_the_client_as_a_todo_activity()
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
                          {"content":"Implement the validator","status":"in_progress"},
                          {"content":"Write the docs","status":"pending"}]}
                        """).RootElement),
            },
            AssistantFinishReason.ToolCalls,
            0));
        steps.Enqueue(new AssistantStep(
            "plan published",
            Array.Empty<ModelToolCall>(),
            AssistantFinishReason.Stop,
            0));

        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(
            dataPaths: tmp.DataPaths,
            modelStepRunner: new ScriptedStepRunner(steps),
            withInProcessBroker: true);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var created = await CreateRunAsync(
            client, sessionId, tmp.Path, "plan this", "key-" + Guid.NewGuid().ToString("N"));
        var runId = created.GetProperty("run_id").GetString()!;

        // Subscribed before the run starts, because an activity frame is transient: the
        // hub fans it out to whoever is listening at that moment and never replays it, so
        // a subscriber that arrives after the run has finished sees nothing. That is how
        // a real client behaves -- it is streaming while the work happens.
        var hub = factory.Services.GetRequiredService<HarnessLiveEventHub>();
        await using var lease = hub.Subscribe(new RunId(runId));

        using var trustResp = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{runId}/trust",
            new
            {
                trust_request_id = created.GetProperty("trust_request_id").GetString(),
                display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
                decision = "approve",
            });
        trustResp.EnsureSuccessStatusCode();

        var status = await WaitForTerminalStatusAsync(client, runId);
        Assert.True(status == "completed", $"final status={status}");

        // The durable stream records the tool completion like any other tool.
        using var eventsResp = await client.GetAsync($"/agent/v1/runs/{runId}/events");
        var eventsBody = await eventsResp.Content.ReadAsStringAsync();
        Assert.True(eventsResp.IsSuccessStatusCode);
        Assert.Contains("tool.execution_completed", eventsBody, StringComparison.Ordinal);

        // And the list reached the client as an activity, which is what the checklist in
        // the transcript renders from. The rows are asserted, not just the frame name: an
        // activity that arrived empty would pass a name-only check.
        var todos = await ReadTodoActivityAsync(lease);
        Assert.True(
            todos is not null,
            LastFrames.Count == 0
                ? "no activity frame was fanned out to the subscriber"
                : "no todo activity was fanned out; saw: " + string.Join(", ", LastFrames));

        Assert.Equal(3, todos!.Value.GetArrayLength());
        Assert.Equal("Read the failing test", todos.Value[0].GetProperty("content").GetString());
        Assert.Equal("completed", todos.Value[0].GetProperty("status").GetString());
        Assert.Equal("in_progress", todos.Value[1].GetProperty("status").GetString());
        Assert.Equal("pending", todos.Value[2].GetProperty("status").GetString());
    }




    /// <summary>
    /// The frames the last read saw, so a missing row reports what arrived instead of only
    /// that something did not.
    /// </summary>
    private static List<string> LastFrames { get; } = [];

    /// <summary>
    /// Reads activity frames until one carries the task list.
    /// </summary>
    /// <remarks>
    /// The kind alone is not enough to identify the list. The tool row and the row holding
    /// the list are both of kind todo -- the tool row goes out first, in its started state,
    /// with no list on it -- so the frame is identified by carrying rows, not by its name.
    /// Returning on the first frame of the right kind read the tool row and found nothing.
    ///
    /// Returns null when the frames run out or the wait expires, so the caller reports a
    /// missing list rather than an unrelated timeout.
    /// </remarks>
    private static async Task<JsonElement?> ReadTodoActivityAsync(HarnessLiveEventHub.HubLease lease)
    {
        LastFrames.Clear();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            TransientFrame frame;
            try
            {
                frame = await lease.ActivityReader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ChannelClosedException)
            {
                return null;
            }

            using var wire = JsonDocument.Parse(frame.Data);
            string kind = wire.RootElement.TryGetProperty("kind", out var wireKind)
                ? wireKind.GetString() ?? "?"
                : "?";
            LastFrames.Add($"{frame.Kind}/{kind}");

            if (!string.Equals(frame.Kind, "agent.activity", StringComparison.Ordinal))
            {
                continue;
            }

            if (wire.RootElement.TryGetProperty("todos", out var todos)
                && todos.ValueKind == JsonValueKind.Array)
            {
                return todos.Clone();
            }
        }

        return null;
    }

    private static async Task<string?> WaitForTerminalStatusAsync(HttpClient client, string runId)
    {
        string? status = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            using var response = await client.GetAsync($"/agent/v1/runs/{runId}");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            status = document.RootElement.GetProperty("status").GetString();
            if (status is "completed" or "failed")
            {
                break;
            }
        }

        return status;
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/sessions/new", null);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("session_id").GetString()!;
    }

    private static async Task<JsonElement> CreateRunAsync(
        HttpClient client,
        string sessionId,
        string workspacePath,
        string prompt,
        string idempotencyKey)
    {
        Directory.CreateDirectory(workspacePath);
        using var response = await client.PostAsJsonAsync("/agent/v1/runs", new
        {
            session_id = sessionId,
            workspace_path = workspacePath,
            prompt,
            preset = "standard",
            idempotency_key = idempotencyKey,
        });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"create run returned {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed class ScriptedStepRunner : IModelStepRunner
    {
        private readonly Queue<AssistantStep> _steps;

        public ScriptedStepRunner(Queue<AssistantStep> steps) => _steps = steps;

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
            // built, so an empty receipt fails as prompt_contract_invalid. The real runner
            // composes this; the scripted one has to do the same rather than stub it out.
            var receipt = SystemPromptComposer.Compose(request.PromptContext).Receipt;
            return Task.FromResult(new ModelStepResult(_steps.Dequeue(), receipt));
        }
    }

    /// <summary>
    /// An isolated workspace and data root, so the run cannot touch real user state.
    /// </summary>
    private sealed class TempWorkspace : IDisposable
    {
        private readonly string _root;

        private TempWorkspace(string root, string workspacePath, RuBackend.Business.DataPaths dataPaths)
        {
            _root = root;
            Path = workspacePath;
            DataPaths = dataPaths;
        }

        public string Path { get; }

        public RuBackend.Business.DataPaths DataPaths { get; }

        public static TempWorkspace Create()
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ru-todo-e2e-" + Guid.NewGuid().ToString("N"));
            var workspace = System.IO.Path.Combine(root, "ws");
            Directory.CreateDirectory(workspace);
            var dataRoot = System.IO.Path.Combine(root, "data");
            Directory.CreateDirectory(dataRoot);
            var dataPaths = new RuBackend.Business.DataPaths(
                System.IO.Path.Combine(dataRoot, "settings.json"),
                System.IO.Path.Combine(dataRoot, "sessions"),
                System.IO.Path.Combine(dataRoot, "activity.jsonl"));
            return new TempWorkspace(root, workspace, dataPaths);
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
