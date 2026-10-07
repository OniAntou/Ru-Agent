using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// ask_user semantics over the real host runtime with a scripted model: the park,
/// the answer route, and the restart-safe retained-input resume. Spawns real
/// processes and a shared workspace, so it runs in the non-parallel collection.
/// </summary>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class AgentInputEndpointTests
{
    private static readonly TimeSpan StateDeadline = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task Ask_user_parks_waiting_input_and_the_answer_reaches_the_model()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new RecordingRunner(
                AskUserStep("Which environment?", "staging", "production"),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner, withInProcessBroker: true);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "input park");
            var runId = created.GetProperty("run_id").GetString()!;

            var request = await WaitForInputRequestAsync(client, runId, StateDeadline);
            Assert.NotNull(request);
            var (inputId, question, options) = request!.Value;
            Assert.StartsWith("inp_", inputId);
            Assert.Equal("Which environment?", question);
            Assert.Equal(new[] { "staging", "production" }, options);

            // The park is visible on the run status wire, and the Broker never saw
            // the asked call: no execution event exists before the answer.
            Assert.Equal("waiting_input", await GetStatusAsync(client, runId));
            var eventsBefore = await ReadEventsAsync(client, runId);
            Assert.DoesNotContain(eventsBefore, item => item.GetProperty("event_type").GetString() == "tool.execution_completed");
            Assert.DoesNotContain(eventsBefore, item => item.GetProperty("event_type").GetString() == "tool.execution_requested");

            RuBackend.Harness.AgentV1Service.ClearHostInputsForTests();
            using var answerResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/inputs/{inputId}",
                new { answer = "staging" });
            Assert.Equal(HttpStatusCode.OK, answerResponse.StatusCode);
            var answerJson = await answerResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("answered", answerJson.GetProperty("status").GetString());
            Assert.Equal("running", answerJson.GetProperty("run_state").GetString());

            Assert.True(
                await WaitForStatusAsync(
                    client,
                    runId,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    StateDeadline),
                "run did not complete after the answer");

            var events = await ReadEventsAsync(client, runId);
            Assert.Contains(
                events,
                item => item.GetProperty("event_type").GetString() == "run.state_changed"
                    && item.TryGetProperty("safe_summary", out var summary)
                    && summary.GetString() == "input_resolved");
            var completion = Assert.Single(
                events,
                item => item.GetProperty("event_type").GetString() == "tool.execution_completed");
            Assert.True(completion.TryGetProperty("payload_ref", out var payloadRef));
            using var payloadResponse = await client.GetAsync($"/agent/v1/runs/{runId}/payloads/{payloadRef.GetString()}");
            payloadResponse.EnsureSuccessStatusCode();
            var payloadJson = await payloadResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("staging", payloadJson.GetProperty("content").GetString());

            // The next model step received the answer as that call's tool result.
            Assert.Contains(
                runner.SeenContexts,
                entries => entries.Any(entry => entry.Content?.Contains("staging", StringComparison.Ordinal) == true));
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task Answer_route_rejects_bad_requests_and_stale_or_foreign_ids()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, withInProcessBroker: true);
            var client = factory.CreateAuthenticatedClient();

            var fakeRunId = "run_" + new string('a', 32);
            var fakeInputId = "inp_" + new string('b', 32);
            using var unknownRun = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{fakeRunId}/inputs/{fakeInputId}",
                new { answer = "x" });
            Assert.Equal(HttpStatusCode.NotFound, unknownRun.StatusCode);
            Assert.Equal("run_not_found", (await unknownRun.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());

            using var badRun = await client.PostAsJsonAsync(
                "/agent/v1/runs/not-a-run/inputs/" + fakeInputId,
                new { answer = "x" });
            Assert.Equal(HttpStatusCode.BadRequest, badRun.StatusCode);
            Assert.Equal("invalid_run_id", (await badRun.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());

            using var badInput = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{fakeRunId}/inputs/apr_" + new string('c', 32),
                new { answer = "x" });
            Assert.Equal(HttpStatusCode.BadRequest, badInput.StatusCode);
            Assert.Equal("invalid_input_id", (await badInput.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task Parked_run_answer_rejects_foreign_ids_and_a_replay()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new RecordingRunner(
                AskUserStep("Pick one", "a", "b"),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner, withInProcessBroker: true);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "input stale");
            var runId = created.GetProperty("run_id").GetString()!;

            var request = await WaitForInputRequestAsync(client, runId, StateDeadline);
            Assert.NotNull(request);
            var inputId = request!.Value.InputId;

            using var foreign = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/inputs/inp_" + new string('f', 32),
                new { answer = "a" });
            Assert.Equal((HttpStatusCode)409, foreign.StatusCode);
            Assert.Equal("input_stale", (await foreign.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());

            using var emptyAnswer = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/inputs/{inputId}",
                new { answer = "" });
            Assert.Equal(HttpStatusCode.BadRequest, emptyAnswer.StatusCode);
            Assert.Equal("invalid_answer", (await emptyAnswer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());

            RuBackend.Harness.AgentV1Service.ClearHostInputsForTests();
            using var answer = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/inputs/{inputId}",
                new { answer = "a" });
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

            using var replay = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/inputs/{inputId}",
                new { answer = "a" });
            Assert.Equal((HttpStatusCode)409, replay.StatusCode);
            Assert.Equal("input_stale", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task Waiting_input_run_survives_a_host_restart_and_resumes_retained_proposals()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        File.WriteAllText(Path.Combine(wsPath, "notes.txt"), "retained content");
        BackendWebApplicationFactory? factory = null;
        BackendWebApplicationFactory? restarted = null;
        try
        {
            var runner = new RecordingRunner(AskUserWithReadStep(), FinalStep("done"));
            string runId;
            string inputId;
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner, withInProcessBroker: true);
            using (factory)
            {
                var client = factory.CreateAuthenticatedClient();
                var sessionId = await CreateSessionAsync(client);
                var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "input restart");
                runId = created.GetProperty("run_id").GetString()!;

                var request = await WaitForInputRequestAsync(client, runId, StateDeadline);
                Assert.NotNull(request);
                inputId = request!.Value.InputId;
                Assert.Equal("waiting_input", await GetStatusAsync(client, runId));
            }

            // A new host process: nothing in memory carries the park, so the answer
            // must work from durable state alone.
            restarted = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner, withInProcessBroker: true);
            var restartedClient = restarted.CreateAuthenticatedClient();
            RuBackend.Harness.AgentV1Service.ClearHostInputsForTests();
            using var answer = await restartedClient.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/inputs/{inputId}",
                new { answer = "staging" });
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

            Assert.True(
                await WaitForStatusAsync(
                    restartedClient,
                    runId,
                    new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                    StateDeadline),
                "restarted host did not complete the run after the answer");
            Assert.Equal("completed", await GetStatusAsync(restartedClient, runId));

            // The asked call plus the retained read both completed after restart.
            var events = await ReadEventsAsync(restartedClient, runId);
            Assert.Equal(
                2,
                events.Count(item => item.GetProperty("event_type").GetString() == "tool.execution_completed"));
            Assert.Contains(
                runner.SeenContexts,
                entries => entries.Any(entry => entry.Content?.Contains("retained content", StringComparison.Ordinal) == true));
        }
        finally
        {
            restarted?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private static string CreateTempWorkspace(out RuBackend.Business.DataPaths dataPaths)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-input-test-" + Guid.NewGuid().ToString("N"));
        var ws = Path.Combine(root, "ws");
        Directory.CreateDirectory(ws);
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(dataRoot);
        dataPaths = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, Path.Combine(root, "logs"));
        return ws;
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

    private static async Task<(string InputId, string Question, string[] Options)?> WaitForInputRequestAsync(
        HttpClient client,
        string runId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var events = await ReadEventsAsync(client, runId);
            var inputEvent = events.LastOrDefault(item =>
                item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("event_type", out var type)
                && type.GetString() == "input.requested");
            if (inputEvent.ValueKind == JsonValueKind.Object
                && inputEvent.TryGetProperty("payload_ref", out var payloadRef)
                && payloadRef.ValueKind == JsonValueKind.String)
            {
                using var payloadResponse = await client.GetAsync(
                    $"/agent/v1/runs/{runId}/payloads/{payloadRef.GetString()}");
                if (payloadResponse.IsSuccessStatusCode)
                {
                    var payload = await payloadResponse.Content.ReadFromJsonAsync<JsonElement>();
                    using var document = JsonDocument.Parse(payload.GetProperty("content").GetString()!);
                    var root = document.RootElement;
                    return (
                        root.GetProperty("input_id").GetString()!,
                        root.GetProperty("question").GetString()!,
                        root.GetProperty("options").EnumerateArray().Select(item => item.GetString()!).ToArray());
                }
            }

            await Task.Delay(200);
        }

        return null;
    }

    private static async Task<List<JsonElement>> ReadEventsAsync(HttpClient client, string runId)
    {
        using var resp = await client.GetAsync($"/agent/v1/runs/{runId}/events?live=0");
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

    private static async Task<bool> WaitForStatusAsync(
        HttpClient client,
        string runId,
        IReadOnlySet<string> states,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            if (states.Contains(await GetStatusAsync(client, runId)))
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

    private sealed class RecordingRunner : IModelStepRunner
    {
        private readonly Queue<AssistantStep> _steps = new();

        public RecordingRunner(params AssistantStep[] steps)
        {
            foreach (var step in steps)
            {
                _steps.Enqueue(step);
            }
        }

        public List<IReadOnlyList<ModelContextEntry>> SeenContexts { get; } = new();

        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            SeenContexts.Add(request.Context.Entries.ToArray());
            var step = _steps.Count > 0 ? _steps.Dequeue() : FinalStep("script exhausted; done");
            return Task.FromResult(new ModelStepResult(
                step,
                SystemPromptComposer
                    .Compose(request.PromptContext, nativeToolCalling: true)
                    .Receipt));
        }
    }

    private static AssistantStep AskUserStep(string question, params string[] options) =>
        new(
            null,
            new[]
            {
                new ModelToolCall(
                    "provider-ask",
                    "ask_user",
                    "1.0.0",
                    JsonDocument.Parse(JsonSerializer.Serialize(new { question, options })).RootElement.Clone()),
            },
            AssistantFinishReason.ToolCalls,
            0);

    private static AssistantStep AskUserWithReadStep() =>
        new(
            null,
            new[]
            {
                new ModelToolCall(
                    "provider-ask",
                    "ask_user",
                    "1.0.0",
                    JsonDocument.Parse("{\"question\":\"Which environment?\",\"options\":[\"staging\"]}").RootElement.Clone()),
                new ModelToolCall(
                    "provider-read",
                    "read",
                    "1.0.0",
                    JsonDocument.Parse("{\"path\":\"notes.txt\"}").RootElement.Clone()),
            },
            AssistantFinishReason.ToolCalls,
            0);

    private static AssistantStep FinalStep(string text) =>
        new(text, Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0);

    private static void SqliteConnectionClear(RuBackend.Business.DataPaths dataPaths)
    {
        try { SqliteTestPools.ClearHarnessDatabase(dataPaths.AgentHarnessDatabase); } catch { }
    }
}
