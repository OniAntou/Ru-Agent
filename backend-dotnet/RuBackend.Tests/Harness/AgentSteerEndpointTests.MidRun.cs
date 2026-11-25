using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Steering;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Independent steering checks beyond the first endpoint test: a steer that arrives
/// while a model step is genuinely in flight, the stop path, and the queue contract
/// (send order plus the full-queue refusal) over the real host runtime.
/// </summary>
public sealed partial class AgentInputEndpointTests
{
    [Fact]
    public async Task A_steer_during_a_model_step_waits_for_the_boundary_and_then_reaches_the_model()
    {
        const string steerText = "Also run the migration tests";
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new GatedRunner();
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "steer mid-step");
            var runId = created.GetProperty("run_id").GetString()!;

            // Step 1 parks on a question. Answering it starts step 2, which the runner
            // holds open so the steer really arrives while a model step is in flight.
            var first = await WaitForInputRequestAsync(client, runId, StateDeadline);
            Assert.NotNull(first);
            using (var answer = await client.PostAsJsonAsync(
                       $"/agent/v1/runs/{runId}/inputs/{first!.Value.InputId}", new { answer = "staging" }))
            {
                Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            }

            Assert.True(await WaitUntilAsync(() => runner.StepCount >= 2, StateDeadline), "step 2 never started");
            var inFlight = runner.ContextAt(1);
            Assert.DoesNotContain(inFlight, entry => entry.Content == steerText);

            var steerId = NewSteerId();
            using (var steer = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = steerId, text = steerText }))
            {
                Assert.Equal(HttpStatusCode.Accepted, steer.StatusCode);
            }

            // Still in flight: nothing reached the step that was already running.
            Assert.DoesNotContain(runner.ContextAt(1), entry => entry.Content == steerText);
            Assert.Equal(RunSteerContract.StatePending,
                (await ListSteersAsync(client, runId)).Single().GetProperty("state").GetString());

            // Step 2 ends on a second question, so the run parks again and the steer
            // keeps waiting instead of being injected into a settled step.
            runner.ReleaseStep();
            var second = await WaitForDifferentInputRequestAsync(client, runId, first.Value.InputId, StateDeadline);
            Assert.NotNull(second);
            Assert.DoesNotContain(runner.ContextAt(1), entry => entry.Content == steerText);

            using (var answer = await client.PostAsJsonAsync(
                       $"/agent/v1/runs/{runId}/inputs/{second!.Value.InputId}", new { answer = "yes" }))
            {
                var answerBody = await answer.Content.ReadAsStringAsync();
                var status = await StatusAsync(client, runId);
                Assert.True(answer.StatusCode == HttpStatusCode.OK,
                    $"second answer failed: {answer.StatusCode} status={status} body={answerBody}");
            }

            Assert.True(await WaitForStatusAsync(client, runId,
                new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, StateDeadline));

            // The step after the boundary read it, as a user entry.
            var afterBoundary = runner.ContextAt(runner.StepCount - 1);
            Assert.Contains(afterBoundary, entry =>
                entry.Role == ModelContextRole.User && entry.Content == steerText);
            Assert.Equal(RunSteerContract.StateDelivered,
                (await ListSteersAsync(client, runId)).Single().GetProperty("state").GetString());
            Assert.Equal("completed", await StatusAsync(client, runId));
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task A_steer_still_pending_when_the_run_is_stopped_reads_as_returned_and_never_reaches_the_model()
    {
        const string steerText = "Stop after this and summarize";
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new RecordingRunner(AskUserStep("Which environment?", "staging", "production"), FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "steer then stop");
            var runId = created.GetProperty("run_id").GetString()!;
            Assert.NotNull(await WaitForInputRequestAsync(client, runId, StateDeadline));

            using (var steer = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = NewSteerId(), text = steerText }))
            {
                Assert.Equal(HttpStatusCode.Accepted, steer.StatusCode);
            }

            using (var cancel = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/cancel", new { }))
            {
                Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
            }

            Assert.True(await WaitForStatusAsync(client, runId,
                new HashSet<string>(StringComparer.Ordinal) { "cancelled", "failed", "completed" }, StateDeadline));

            // Returned, so a client can send it as the next turn; never delivered.
            var steerRow = (await ListSteersAsync(client, runId)).Single();
            Assert.Equal(RunSteerContract.StateReturned, steerRow.GetProperty("state").GetString());
            Assert.DoesNotContain(
                runner.SeenContexts.SelectMany(entries => entries),
                entry => entry.Content == steerText);
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task Steers_are_listed_in_send_order_and_a_full_queue_is_refused_over_http()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new RecordingRunner(AskUserStep("Which environment?", "staging"), FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "steer queue");
            var runId = created.GetProperty("run_id").GetString()!;
            var request = await WaitForInputRequestAsync(client, runId, StateDeadline);
            Assert.NotNull(request);

            var texts = Enumerable.Range(0, RunSteerContract.MaxPending).Select(index => "steer " + index).ToArray();
            foreach (var text in texts)
            {
                using var steer = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                    new { steer_id = NewSteerId(), text });
                Assert.Equal(HttpStatusCode.Accepted, steer.StatusCode);
            }

            using (var over = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = NewSteerId(), text = "one too many" }))
            {
                Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
                Assert.Equal("steer_queue_full",
                    (await over.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());
            }

            var listed = await ListSteersAsync(client, runId);
            Assert.Equal(texts, listed.Select(row => row.GetProperty("text").GetString()));

            using (var answer = await client.PostAsJsonAsync(
                       $"/agent/v1/runs/{runId}/inputs/{request!.Value.InputId}", new { answer = "staging" }))
            {
                Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            }

            Assert.True(await WaitForStatusAsync(client, runId,
                new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, StateDeadline));

            // One boundary delivers every pending steer, in the order they were sent.
            var nextStep = runner.SeenContexts[^1];
            var delivered = nextStep
                .Where(entry => entry.Role == ModelContextRole.User
                    && texts.Contains(entry.Content, StringComparer.Ordinal))
                .Select(entry => entry.Content)
                .ToArray();
            Assert.Equal(texts, delivered);
            Assert.All(await ListSteersAsync(client, runId),
                row => Assert.Equal(RunSteerContract.StateDelivered, row.GetProperty("state").GetString()));
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    private static async Task<string> StatusAsync(HttpClient client, string runId)
    {
        using var resp = await client.GetAsync($"/agent/v1/runs/{runId}");
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("status").GetString()!;
    }

    /// <summary>
    /// Waits for an input request other than the one already answered: the newest
    /// event can still be the previous park while the next step has not asked yet.
    /// </summary>
    private static async Task<(string InputId, string Question, string[] Options)?> WaitForDifferentInputRequestAsync(
        HttpClient client,
        string runId,
        string previousInputId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var request = await WaitForInputRequestAsync(client, runId, TimeSpan.FromSeconds(2));
            if (request is { } found && !string.Equals(found.InputId, previousInputId, StringComparison.Ordinal))
            {
                return found;
            }

            await Task.Delay(50);
        }

        return null;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }

        return condition();
    }

    /// <summary>Holds its second step open so a steer can arrive while the step runs.</summary>
    private sealed class GatedRunner : IModelStepRunner
    {
        private readonly TaskCompletionSource _secondStep = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IReadOnlyList<ModelContextEntry>> _contexts = new();
        private int _steps;

        public int StepCount => Volatile.Read(ref _steps);

        public IReadOnlyList<ModelContextEntry> ContextAt(int index)
        {
            lock (_contexts)
            {
                return index >= 0 && index < _contexts.Count ? _contexts[index] : Array.Empty<ModelContextEntry>();
            }
        }

        public void ReleaseStep() => _secondStep.TrySetResult();

        public async Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            lock (_contexts)
            {
                _contexts.Add(request.Context.Entries.ToArray());
            }

            var index = Interlocked.Increment(ref _steps);
            if (index == 2)
            {
                await _secondStep.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var step = index switch
            {
                1 => AskUserStep("Which environment?", "staging"),
                2 => AskUserStep("Confirm?", "yes"),
                _ => FinalStep("done"),
            };
            return new ModelStepResult(
                step,
                SystemPromptComposer.Compose(request.PromptContext).Receipt);
        }
    }
}
