using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;
using RuBackend.Business;
using RuBackend.Diagnostics;
using Xunit;

namespace RuBackend.Tests.Diagnostics;

public sealed class DiagnosticEndpointsTests
{
    [Fact]
    public async Task GetLogs_returns_paginated_diagnostic_events_and_cursor()
    {
        using var factory = new BackendWebApplicationFactory();
        var paths = factory.Services.GetRequiredService<DataPaths>();
        Directory.CreateDirectory(paths.LogRoot);

        var logFile = Path.Combine(paths.LogRoot, $"aspnetcore-{DateTimeOffset.UtcNow.AddDays(-1):yyyy-MM-dd}.jsonl");
        var lines = new[]
        {
            """{"event_id":"evt_1","ts":1000.0,"iso":"2026-08-20T00:00:00.000Z","level":"info","component":"host","event_code":"app.start","correlation_id":"c_test_diag_01"}""",
            """{"event_id":"evt_2","ts":1001.0,"iso":"2026-08-20T00:00:01.000Z","level":"error","component":"worker.voice","event_code":"voice.failed","status":"failed","error_code":"timeout","correlation_id":"c_test_diag_01"}""",
        };
        await File.AppendAllLinesAsync(logFile, lines);

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/diagnostics/logs?correlation_id=c_test_diag_01&limit=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DiagnosticLogResult>();
        Assert.NotNull(result);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal("evt_1", result.Events[0].EventId);
        Assert.Equal("evt_2", result.Events[1].EventId);
    }

    [Fact]
    public async Task GetLogs_filters_by_level_component_and_event_code()
    {
        using var factory = new BackendWebApplicationFactory();
        var paths = factory.Services.GetRequiredService<DataPaths>();
        Directory.CreateDirectory(paths.LogRoot);

        var logFile = Path.Combine(paths.LogRoot, $"aspnetcore-{DateTimeOffset.UtcNow.AddDays(-1):yyyy-MM-dd}.jsonl");
        var lines = new[]
        {
            """{"event_id":"evt_10","ts":1000.0,"iso":"2026-08-20T00:00:00.000Z","level":"info","component":"host","event_code":"app.start","correlation_id":"c_test_diag_02"}""",
            """{"event_id":"evt_20","ts":1001.0,"iso":"2026-08-20T00:00:01.000Z","level":"error","component":"worker.voice","event_code":"voice.failed","correlation_id":"c_test_diag_02"}""",
        };
        await File.AppendAllLinesAsync(logFile, lines);

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/diagnostics/logs?correlation_id=c_test_diag_02&level=error");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DiagnosticLogResult>();
        Assert.NotNull(result);
        Assert.Single(result.Events);
        Assert.Equal("evt_20", result.Events[0].EventId);
    }

    [Fact]
    public async Task ExportLogs_returns_ndjson_attachment()
    {
        using var factory = new BackendWebApplicationFactory();
        var paths = factory.Services.GetRequiredService<DataPaths>();
        Directory.CreateDirectory(paths.LogRoot);

        var logFile = Path.Combine(paths.LogRoot, $"aspnetcore-{DateTimeOffset.UtcNow.AddDays(-1):yyyy-MM-dd}.jsonl");
        var lines = new[]
        {
            """{"event_id":"evt_1","ts":1000.0,"iso":"2026-08-20T00:00:00.000Z","level":"info","component":"host","event_code":"app.start"}""",
        };
        await File.AppendAllLinesAsync(logFile, lines);

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/diagnostics/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("evt_1", content);
        Assert.Contains("app.start", content);
    }

    [Fact]
    public async Task GetStatus_returns_queue_and_disk_metrics()
    {
        using var factory = new BackendWebApplicationFactory();
        var paths = factory.Services.GetRequiredService<DataPaths>();
        Directory.CreateDirectory(paths.LogRoot);

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/diagnostics/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("healthy", root["status"]!.GetValue<string>());
        Assert.True(root.ContainsKey("total_log_files"));
        Assert.True(root.ContainsKey("total_size_bytes"));
        Assert.True(root.ContainsKey("writer"));
    }

    [Fact]
    public async Task GetStatus_reports_live_activity_loss()
    {
        // Live activity is best-effort by design: the hub drops the oldest frame rather
        // than block a slow reader, and the projector swallows a sink that throws. Those
        // are the right behaviours, but without these counters a frame that never
        // arrived was indistinguishable from one that was never published.
        //
        // The publisher counter is process-wide, so this reads a delta rather than
        // assuming a fresh process: other tests in this assembly publish too.
        using var factory = new BackendWebApplicationFactory();
        var paths = factory.Services.GetRequiredService<DataPaths>();
        Directory.CreateDirectory(paths.LogRoot);

        using var client = factory.CreateAuthenticatedClient();

        static async Task<long> ReadPublisherFailuresAsync(HttpClient client)
        {
            var response = await client.GetAsync("/diagnostics/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            return root["live_activity"]!.AsObject()["publisher_failures"]!.GetValue<long>();
        }

        long before = await ReadPublisherFailuresAsync(client);

        // Force one counted loss through the same seam the runtime uses.
        AgentActivityProjector.PublishSafe(
            new ThrowingActivitySink(),
            new RunId("run_" + new string('a', 32)),
            AgentActivityProjector.ProjectModelStep(
                new ModelStepId("step_" + new string('b', 32)),
                AgentActivityState.Started));

        long after = await ReadPublisherFailuresAsync(client);
        Assert.Equal(before + 1, after);

        var live = JsonNode.Parse(
            await (await client.GetAsync("/diagnostics/status")).Content.ReadAsStringAsync())!
            .AsObject()["live_activity"]!.AsObject();

        Assert.True(live.ContainsKey("dropped_frames"));
        Assert.True(live.ContainsKey("dropped_activity_frames"));
        Assert.True(live["dropped_frames"]!.GetValue<long>() >= 0);
        Assert.True(live["dropped_activity_frames"]!.GetValue<long>() >= 0);
    }

    /// <summary>A sink that fails the way a disconnected live subscriber does.</summary>
    private sealed class ThrowingActivitySink : IAgentActivitySink
    {
        public void Publish(RunId runId, AgentActivity activity) =>
            throw new InvalidOperationException("sink unavailable");
    }

    [Theory]
    [InlineData("/diagnostics/logs?limit=5001", "diagnostic_limit_out_of_range")]
    [InlineData("/diagnostics/logs?since=not-a-date", "diagnostic_since_invalid")]
    [InlineData("/diagnostics/logs?cursor=bad", "diagnostic_cursor_invalid")]
    public async Task Diagnostic_query_validation_returns_a_safe_bad_request(
        string path,
        string expectedErrorCode)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(expectedErrorCode, json!["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Export_rejects_a_response_that_exceeds_the_byte_cap()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-diag-export-cap-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DataPaths(Path.Combine(root, "settings.json"),
                Path.Combine(root, "sessions"),
                Path.Combine(root, "activity.jsonl"));
            Directory.CreateDirectory(paths.LogRoot);
            var paddedIso = "2026-08-20T" + new string('x', 480);
            var lines = Enumerable.Range(0, 5_000)
                .Select(index =>
                    $"{{\"event_id\":\"evt_{index}\",\"ts\":{index + 1},\"iso\":\"{paddedIso}\",\"level\":\"info\",\"component\":\"host\",\"event_code\":\"export.test\"}}")
                .ToArray();
            await File.WriteAllLinesAsync(
                Path.Combine(paths.LogRoot, $"aspnetcore-{DateTimeOffset.UtcNow:yyyy-MM-dd}.jsonl"),
                lines);

            using var factory = new BackendWebApplicationFactory(dataPaths: paths);
            using var client = factory.CreateAuthenticatedClient();
            var response = await client.GetAsync("/diagnostics/export");

            Assert.Equal((HttpStatusCode)413, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal("diagnostic_export_too_large", json!["error_code"]!.GetValue<string>());
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
