using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Steering (ADR 0056) over the real host runtime: a steer sent while the run waits is
/// held durably, then reaches the model as a user entry at the next step boundary.
/// </summary>
public sealed partial class AgentInputEndpointTests
{
    private static string NewSteerId() => "steer_" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_steer_sent_while_the_run_waits_reaches_the_next_model_step_as_user_text()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new RecordingRunner(
                AskUserStep("Which environment?", "staging", "production"),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "steer while waiting");
            var runId = created.GetProperty("run_id").GetString()!;
            var request = await WaitForInputRequestAsync(client, runId, StateDeadline);
            Assert.NotNull(request);

            var steerId = NewSteerId();
            using (var steerResponse = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = steerId, text = "Also skip the migration step" }))
            {
                Assert.Equal(HttpStatusCode.Accepted, steerResponse.StatusCode);
                var body = await steerResponse.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("pending", body.GetProperty("state").GetString());
                Assert.Equal("payload_" + steerId["steer_".Length..], body.GetProperty("delivered_payload_id").GetString());
            }

            // A retried send answers as the first one did.
            using (var retry = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = steerId, text = "Also skip the migration step" }))
            {
                Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            }

            Assert.Equal("pending", (await ListSteersAsync(client, runId)).Single().GetProperty("state").GetString());
            using (var answer = await client.PostAsJsonAsync(
                       $"/agent/v1/runs/{runId}/inputs/{request!.Value.InputId}", new { answer = "staging" }))
            {
                Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            }

            Assert.True(await WaitForStatusAsync(client, runId,
                new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, StateDeadline));

            Assert.Contains(runner.SeenContexts, entries => entries.Any(entry =>
                entry.Role == ModelContextRole.User && entry.Content == "Also skip the migration step"));
            Assert.Equal("delivered", (await ListSteersAsync(client, runId)).Single().GetProperty("state").GetString());
            var events = await ReadEventsAsync(client, runId);
            Assert.Contains(events, item => item.GetProperty("event_type").GetString() == "run.note_added"
                && item.TryGetProperty("safe_summary", out var summary) && summary.GetString() == "user_steer");

            // The run has ended: a later steer belongs in a new turn.
            using var late = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                new { steer_id = NewSteerId(), text = "too late" });
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Equal("run_not_steerable",
                (await late.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error_code").GetString());

            using var replay = await client.GetAsync($"/agent/v1/runs/{runId}/activities");
            replay.EnsureSuccessStatusCode();
            Assert.Contains("Also skip the migration step", await replay.Content.ReadAsStringAsync());
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task A_prompt_hook_that_blocks_the_steer_answers_422_and_stores_nothing()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        var home = Directory.CreateTempSubdirectory("ru-steer-hook-home").FullName;
        BackendWebApplicationFactory? factory = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".ru"));
            File.WriteAllText(Path.Combine(home, ".ru", "hooks.json"), """{"hooks":{"UserPromptSubmit":[{"command":"guard"}]}}""");
            var hooks = new RuBackend.Harness.Hooks.RunLifecycleHooks(new BlockSteerRunner(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RuBackend.Harness.Hooks.RunLifecycleHooks>.Instance,
                home, CancellationToken.None);
            var runner = new RecordingRunner(AskUserStep("Which environment?", "staging", "production"), FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner,
                configureServices: services =>
                {
                    Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
                        .RemoveAll<RuBackend.Harness.Hooks.RunLifecycleHooks>(services);
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, hooks);
                });
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "steer guarded");
            var runId = created.GetProperty("run_id").GetString()!;
            Assert.NotNull(await WaitForInputRequestAsync(client, runId, StateDeadline));

            using var blocked = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                new { steer_id = NewSteerId(), text = "deploy to production" });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
            var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("prompt_blocked_by_hook", body.GetProperty("error_code").GetString());
            Assert.Equal("No steering to production.", body.GetProperty("message").GetString());
            Assert.Empty(await ListSteersAsync(client, runId));
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            try { Directory.Delete(home, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    /// <summary>Lets the run's own prompt through and refuses only the steer.</summary>
    private sealed class BlockSteerRunner : RuBackend.Harness.Hooks.IHookRunner
    {
        public Task<RuBackend.Harness.Hooks.HookResult> RunAsync(RuBackend.Harness.Hooks.HookDefinition hook,
            string stdinJson, string workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(stdinJson.Contains("production", StringComparison.Ordinal)
                ? new RuBackend.Harness.Hooks.HookResult(RuBackend.Harness.Hooks.HookResult.BlockExitCode, "No steering to production.", TimedOut: false)
                : new RuBackend.Harness.Hooks.HookResult(0, string.Empty, TimedOut: false));
    }

    [Fact]
    public async Task The_steer_route_rejects_malformed_and_unknown_requests()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: new RecordingRunner());
            var client = factory.CreateAuthenticatedClient();
            var unknownRun = "run_" + new string('a', 32);

            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, "/agent/v1/runs/not-a-run/steers",
                new { steer_id = NewSteerId(), text = "x" }));
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, $"/agent/v1/runs/{unknownRun}/steers",
                new { steer_id = "steer_UPPER", text = "x" }));
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, $"/agent/v1/runs/{unknownRun}/steers",
                new { steer_id = NewSteerId(), text = "  " }));
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, $"/agent/v1/runs/{unknownRun}/steers",
                new { steer_id = NewSteerId(), text = new string('x', RuAgent.Protocol.Harness.AgentSteerLimits.MaxCharacters + 1) }));
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, $"/agent/v1/runs/{unknownRun}/steers",
                new { steer_id = NewSteerId(), text = "x", images = "data:image/png;base64,AAAA" }));
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, $"/agent/v1/runs/{unknownRun}/steers",
                new { steer_id = NewSteerId(), text = "x", images = new[] { "data:image/gif;base64,AAAA" } }));
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, $"/agent/v1/runs/{unknownRun}/steers",
                new { steer_id = NewSteerId(), text = "x", images = Enumerable.Repeat("data:image/png;base64,AAAA",
                    RuAgent.Protocol.Harness.AgentSteerLimits.MaxImages + 1).ToArray() }));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, $"/agent/v1/runs/{unknownRun}/steers",
                new { steer_id = NewSteerId(), text = "x" }));
            using var list = await client.GetAsync($"/agent/v1/runs/{unknownRun}/steers");
            Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        return response.StatusCode;
    }

    private static async Task<List<JsonElement>> ListSteersAsync(HttpClient client, string runId)
    {
        using var response = await client.GetAsync($"/agent/v1/runs/{runId}/steers");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("steers").EnumerateArray().Select(item => item.Clone()).ToList();
    }
}
