using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RuAgent.Harness.Core.Steering;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// A retried send is the same steer (ADR 0056): it answers as the first request did even
/// after the run has read the steer or stopped, and the user's prompt hooks see it once.
/// </summary>
public sealed partial class AgentInputEndpointTests
{
    [Fact]
    public async Task A_retry_after_the_run_ended_answers_as_the_first_send_and_never_409s()
    {
        const string steerText = "stop after this";
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new RecordingRunner(AskUserStep("Which environment?", "staging"), FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "retry a steer");
            var runId = created.GetProperty("run_id").GetString()!;
            Assert.NotNull(await WaitForInputRequestAsync(client, runId, StateDeadline));

            var steerId = NewSteerId();
            using (var first = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = steerId, text = steerText }))
            {
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            }

            using (var cancel = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/cancel", new { }))
            {
                Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
            }

            Assert.True(await WaitForStatusAsync(client, runId,
                new HashSet<string>(StringComparer.Ordinal) { "cancelled", "failed", "completed" }, StateDeadline));
            Assert.Equal(RunSteerContract.StateReturned,
                (await ListSteersAsync(client, runId)).Single().GetProperty("state").GetString());

            // The response was lost and the client retries: it is the same stored steer, so the
            // send is answered (200) with the state the list reports, not run_not_steerable.
            using (var retry = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = steerId, text = steerText }))
            {
                Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
                var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(RunSteerContract.StateReturned, body.GetProperty("state").GetString());
            }

            // The same id with other text is still a conflict, never a second message.
            using (var conflict = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = steerId, text = "something else" }))
            {
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                Assert.Equal("idempotency_conflict",
                    (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());
            }

            // A new steer on the ended run is a new message and still belongs to a new turn.
            using (var late = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = NewSteerId(), text = "too late" }))
            {
                Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
                Assert.Equal("run_not_steerable",
                    (await late.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());
            }
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task A_retry_does_not_run_the_prompt_hooks_again()
    {
        const string marker = "hook-marker";
        var wsPath = CreateTempWorkspace(out var dataPaths);
        var home = Directory.CreateTempSubdirectory("ru-steer-retry-hooks").FullName;
        BackendWebApplicationFactory? factory = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".ru"));
            File.WriteAllText(Path.Combine(home, ".ru", "hooks.json"),
                """{"hooks":{"UserPromptSubmit":[{"command":"guard"}]}}""");
            var runner = new CountingHookRunner(marker);
            var hooks = new RuBackend.Harness.Hooks.RunLifecycleHooks(runner,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RuBackend.Harness.Hooks.RunLifecycleHooks>.Instance,
                home, CancellationToken.None);
            factory = new BackendWebApplicationFactory(
                dataPaths: dataPaths,
                modelStepRunner: new RecordingRunner(AskUserStep("Which environment?", "staging"), FinalStep("done")),
                configureServices: services =>
                {
                    Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
                        .RemoveAll<RuBackend.Harness.Hooks.RunLifecycleHooks>(services);
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, hooks);
                });
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "hooks run once");
            var runId = created.GetProperty("run_id").GetString()!;
            Assert.NotNull(await WaitForInputRequestAsync(client, runId, StateDeadline));

            var steerId = NewSteerId();
            using (var first = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = steerId, text = marker }))
            {
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            }

            Assert.Equal(1, runner.SteerHookCalls);

            using (var retry = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = steerId, text = marker }))
            {
                Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            }

            // A retried send is answered from the stored steer: the user's hook side effects
            // must not run twice for one message.
            Assert.Equal(1, runner.SteerHookCalls);

            // A genuinely new steer still passes the hooks.
            using (var second = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = NewSteerId(), text = marker }))
            {
                Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
            }

            Assert.Equal(2, runner.SteerHookCalls);
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            try { Directory.Delete(home, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    /// <summary>Counts the prompt-hook runs that carried the steer's text.</summary>
    private sealed class CountingHookRunner(string marker) : RuBackend.Harness.Hooks.IHookRunner
    {
        private int _steerHookCalls;

        public int SteerHookCalls => Volatile.Read(ref _steerHookCalls);

        public Task<RuBackend.Harness.Hooks.HookResult> RunAsync(
            RuBackend.Harness.Hooks.HookDefinition hook,
            string stdinJson,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            if (stdinJson.Contains(marker, StringComparison.Ordinal))
                Interlocked.Increment(ref _steerHookCalls);
            return Task.FromResult(new RuBackend.Harness.Hooks.HookResult(0, string.Empty, TimedOut: false));
        }
    }
}
