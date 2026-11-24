using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>A steer carries images as a new run's prompt does (ADR 0056 amendment).</summary>
public sealed partial class AgentInputEndpointTests
{
    // A 1x1 PNG, so the Host's image checks see a real data URL.
    private const string OnePixelPng =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    [Fact]
    public async Task A_steer_with_images_reaches_the_model_saying_how_many_came_with_it()
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
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "steer with an image");
            var runId = created.GetProperty("run_id").GetString()!;
            var request = await WaitForInputRequestAsync(client, runId, StateDeadline);
            Assert.NotNull(request);

            // Text and images, then images alone.
            using (var steer = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = NewSteerId(), text = "Match this layout", images = new[] { OnePixelPng, OnePixelPng } }))
            {
                Assert.Equal(HttpStatusCode.Accepted, steer.StatusCode);
            }

            using (var imageOnly = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = NewSteerId(), text = "", images = new[] { OnePixelPng } }))
            {
                Assert.Equal(HttpStatusCode.Accepted, imageOnly.StatusCode);
            }

            Assert.Equal(new[] { 2, 1 }, (await ListSteersAsync(client, runId))
                .Select(item => item.GetProperty("image_count").GetInt32()));
            using (var answer = await client.PostAsJsonAsync(
                       $"/agent/v1/runs/{runId}/inputs/{request!.Value.InputId}", new { answer = "staging" }))
            {
                Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            }

            Assert.True(await WaitForStatusAsync(client, runId,
                new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, StateDeadline));
            var userEntries = runner.SeenContexts.Last()
                .Where(entry => entry.Role == ModelContextRole.User).Select(entry => entry.Content).ToList();
            Assert.Contains(AgentSteerText.WithImages("Match this layout", 2), userEntries);
            Assert.Contains(AgentSteerText.WithImages(string.Empty, 1), userEntries);

            // The delivered activity carries the note, so a reopened chat can show the count.
            using var replay = await client.GetAsync($"/agent/v1/runs/{runId}/activities");
            replay.EnsureSuccessStatusCode();
            Assert.Contains("Images attached to this message: 2", await replay.Content.ReadAsStringAsync());
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }
}
