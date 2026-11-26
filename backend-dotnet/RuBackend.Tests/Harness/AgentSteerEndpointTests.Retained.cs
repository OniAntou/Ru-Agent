using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// A steer the run ended without reading is retained for the session (ADR 0056 amendment):
/// the next run of that session reads it, so no client has to send the message again.
/// </summary>
public sealed partial class AgentInputEndpointTests
{
    [Fact]
    public async Task A_steer_the_stopped_run_never_read_joins_the_next_run_of_the_session()
    {
        const string steerText = "also update the changelog";
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            var runner = new RecordingRunner(
                AskUserStep("Which environment?", "staging"),
                FinalStep("stopped"),
                FinalStep("done"));
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: runner);
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var first = await CreateRunAndTrustAsync(client, sessionId, wsPath, "first turn");
            var firstRun = first.GetProperty("run_id").GetString()!;
            Assert.NotNull(await WaitForInputRequestAsync(client, firstRun, StateDeadline));

            var steerId = NewSteerId();
            using (var steer = await client.PostAsJsonAsync($"/agent/v1/runs/{firstRun}/steers",
                       new { steer_id = steerId, text = steerText, images = new[] { OnePixelPng } }))
            {
                Assert.Equal(HttpStatusCode.Accepted, steer.StatusCode);
            }

            using (var cancel = await client.PostAsJsonAsync($"/agent/v1/runs/{firstRun}/cancel", new { }))
            {
                Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
            }

            Assert.True(await WaitForStatusAsync(client, firstRun,
                new HashSet<string>(StringComparer.Ordinal) { "cancelled", "failed", "completed" }, StateDeadline));

            // The message is not resent as a new turn: the next run of the session reads it.
            var second = await CreateRunAndTrustAsync(client, sessionId, wsPath, "continue");
            var secondRun = second.GetProperty("run_id").GetString()!;
            Assert.True(await WaitForStatusAsync(client, secondRun,
                new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, StateDeadline));

            var entries = runner.SeenContexts[^1].ToList();
            var promptIndex = entries.FindIndex(entry =>
                entry.Role == ModelContextRole.User && entry.Content == "continue");
            var steerIndex = entries.FindIndex(entry =>
                entry.Role == ModelContextRole.User && entry.Content == AgentSteerText.WithImages(steerText, 1));
            Assert.True(promptIndex >= 0, "the new prompt is missing from the run's context");
            Assert.True(steerIndex >= 0, "the retained steer never reached the new run");
            Assert.True(promptIndex < steerIndex, "the retained steer came before the prompt");

            // Delivered once for the session: the run it was sent to reports it read.
            Assert.Equal("delivered",
                (await ListSteersAsync(client, firstRun)).Single().GetProperty("state").GetString());

            // Its image travelled with it, and a reopened chat attributes it to the steer.
            var run = await client.GetFromJsonAsync<JsonElement>($"/agent/v1/runs/{secondRun}");
            var image = Assert.Single(run.GetProperty("image_payloads").EnumerateArray());
            Assert.Equal(steerId, image.GetProperty("steer_id").GetString());
            using var stored = await client.GetAsync(
                $"/agent/v1/runs/{secondRun}/payloads/{image.GetProperty("payload_id").GetString()}");
            stored.EnsureSuccessStatusCode();
            Assert.Equal(OnePixelPng,
                (await stored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("content").GetString());
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }
}
