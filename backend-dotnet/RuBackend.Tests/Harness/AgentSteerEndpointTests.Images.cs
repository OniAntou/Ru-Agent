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
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "steer with an image", [OnePixelPng]);
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

            // A reopened chat finds each steer's own images and can read them back.
            var steerIds = (await ListSteersAsync(client, runId)).Select(item => item.GetProperty("steer_id").GetString()).ToArray();
            var run = await client.GetFromJsonAsync<JsonElement>($"/agent/v1/runs/{runId}");
            var images = run.GetProperty("image_payloads").EnumerateArray()
                .Select(item => (Payload: item.GetProperty("payload_id").GetString()!, Steer: item.GetProperty("steer_id").GetString()))
                .ToArray();
            // The prompt's own image first, then each steer's in delivery order.
            Assert.Equal(new[] { null, steerIds[0], steerIds[0], steerIds[1] }, images.Select(image => image.Steer));
            foreach (var image in images)
            {
                var payload = await client.GetFromJsonAsync<JsonElement>($"/agent/v1/runs/{runId}/payloads/{image.Payload}");
                Assert.Equal(OnePixelPng, payload.GetProperty("content").GetString());
            }
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }
    /// <summary>
    /// A steer delivered before images were stored once kept its waiting copy and gave the
    /// run an unnamed <c>UserImage</c> copy. A reopened chat must still give the prompt only
    /// its own image and the steer its own, not count the copy as the prompt's.
    /// </summary>
    [Fact]
    public async Task A_steer_delivered_with_an_unnamed_image_copy_keeps_its_attribution()
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
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "a prompt image", [OnePixelPng]);
            var runId = created.GetProperty("run_id").GetString()!;
            var request = await WaitForInputRequestAsync(client, runId, StateDeadline);
            Assert.NotNull(request);
            var steerId = NewSteerId();
            using (var steer = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/steers",
                       new { steer_id = steerId, text = "and this", images = new[] { OnePixelPng } }))
            {
                Assert.Equal(HttpStatusCode.Accepted, steer.StatusCode);
            }

            using (var answer = await client.PostAsJsonAsync(
                       $"/agent/v1/runs/{runId}/inputs/{request!.Value.InputId}", new { answer = "staging" }))
            {
                Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            }

            Assert.True(await WaitForStatusAsync(client, runId,
                new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, StateDeadline));
            var promptImage = (await client.GetFromJsonAsync<JsonElement>($"/agent/v1/runs/{runId}"))
                .GetProperty("image_payloads").EnumerateArray()
                .Single(item => item.GetProperty("steer_id").ValueKind == JsonValueKind.Null)
                .GetProperty("payload_id").GetString();

            // Recreate the earlier layout: the delivered image under an id nothing derives.
            string waiting;
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                             $"Data Source={dataPaths.AgentHarnessDatabase};Mode=ReadWrite;Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT payload_id FROM agent_run_steer_images WHERE steer_id=$steer";
                command.Parameters.AddWithValue("$steer", steerId);
                waiting = (string)(await command.ExecuteScalarAsync())!;
                command.CommandText = "UPDATE agent_payloads SET payload_id=$copy WHERE payload_id=$derived";
                command.Parameters.AddWithValue("$copy", "payload_" + Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("$derived", RuAgent.Harness.Core.Steering.RunSteerContract
                    .DeliveredImagePayloadId(new RuAgent.Harness.Core.PayloadId(waiting)).Value);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            var images = (await client.GetFromJsonAsync<JsonElement>($"/agent/v1/runs/{runId}"))
                .GetProperty("image_payloads").EnumerateArray()
                .Select(item => (Payload: item.GetProperty("payload_id").GetString(), Steer: item.GetProperty("steer_id").GetString()))
                .ToArray();
            Assert.Equal(new (string?, string?)[] { (promptImage, null), (waiting, steerId) }, images);
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    /// <summary>
    /// Images a steer may carry together are above the generic 8 MiB body cap. The steers
    /// route used to keep that cap, so two 3 MiB images answered 413 although the
    /// endpoint and the image checks accept them.
    /// </summary>
    [Fact]
    public async Task A_steer_above_the_generic_body_cap_is_accepted()
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
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "steer with large images", null);
            var runId = created.GetProperty("run_id").GetString()!;
            Assert.NotNull(await WaitForInputRequestAsync(client, runId, StateDeadline));

            var image = "data:image/png;base64," + Convert.ToBase64String(new byte[3 * 1024 * 1024]);
            using var content = JsonContent.Create(new { steer_id = NewSteerId(), text = "Match these", images = new[] { image, image } });
            await content.LoadIntoBufferAsync();
            Assert.True(content.Headers.ContentLength > 8 * 1024 * 1024);

            using var steer = await client.PostAsync($"/agent/v1/runs/{runId}/steers", content);

            Assert.Equal(HttpStatusCode.Accepted, steer.StatusCode);
            Assert.Equal(2, Assert.Single(await ListSteersAsync(client, runId)).GetProperty("image_count").GetInt32());
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }

    [Fact]
    public async Task An_image_at_the_per_image_bound_reads_back_whole()
    {
        var wsPath = CreateTempWorkspace(out var dataPaths);
        BackendWebApplicationFactory? factory = null;
        try
        {
            factory = new BackendWebApplicationFactory(dataPaths: dataPaths, modelStepRunner: new RecordingRunner(FinalStep("done")));
            var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var large = "data:image/png;base64," + Convert.ToBase64String(new byte[AgentRunLimits.MaxImageBytes]);
            var created = await CreateRunAndTrustAsync(client, sessionId, wsPath, "a large image", [large]);
            var runId = created.GetProperty("run_id").GetString()!;

            var run = await client.GetFromJsonAsync<JsonElement>($"/agent/v1/runs/{runId}");
            var image = Assert.Single(run.GetProperty("image_payloads").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, image.GetProperty("steer_id").ValueKind);
            using var response = await client.GetAsync($"/agent/v1/runs/{runId}/payloads/{image.GetProperty("payload_id").GetString()}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // The client reads responses through its 8 MiB bound.
            var body = await RuAgent.Client.BoundedHttpResponseReader.ReadAsync(response.Content, default);
            Assert.Equal(large, JsonDocument.Parse(body).RootElement.GetProperty("content").GetString());
        }
        finally
        {
            factory?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(wsPath)!, true); } catch { }
            SqliteConnectionClear(dataPaths);
        }
    }
}
