using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// --json owns stdout: every line a command writes there must be one JSON
/// document, human-facing text goes to stderr, and no console control bytes
/// ever reach the stream.
/// </summary>
public sealed class NdjsonStdoutPurityTests
{
    private static PublicHostSession Session()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var descriptor = new RuAgent.Protocol.Harness.RuntimeDescriptor(
            1,
            "http://127.0.0.1:8000/",
            "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            proc.Id,
            proc.StartTime.ToUniversalTime().ToString("O"),
            new string('a', 32),
            DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(descriptor);
    }

    [Fact]
    public async Task Run_json_writes_line_delimited_json_to_stdout_only()
    {
        var runId = "run_" + new string('a', 32);
        var sessionId = new string('b', 32);
        var workspaceId = "ws_" + new string('d', 32);

        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/sessions/new"))
            {
                var json = JsonSerializer.Serialize(new { status = "ok", session_id = sessionId });
                return Json(json);
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                return Json(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    run_id = runId,
                    session_id = sessionId,
                    workspace_id = workspaceId,
                    status = "running",
                    preset = "standard",
                    created_at = "2026",
                    updated_at = "2026",
                });
                return Json(json, HttpStatusCode.Created);
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                var sse =
                    $"id: 1\ndata: {{\"protocol_version\":1,\"event_id\":\"evt_{new string('e', 32)}\",\"run_id\":\"{runId}\",\"session_id\":\"{sessionId}\",\"sequence\":1,\"event_type\":\"run.state_changed\",\"occurred_at\":\"2026-08-24T00:00:00Z\",\"status\":\"running\",\"safe_summary\":\"started\"}}\n\n" +
                    "event: transient\nid: t2\ndata: {\"kind\":\"assistant.delta\",\"data\":\"xin chào\"}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error_code\":\"not_found\"}"),
            };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var commands = new AgentCommands(Session(), new HttpClient(handler), outWriter, errWriter);
        var exit = await commands.RunAsync(
            new RunCommand(
                Path.GetTempPath().TrimEnd('\\', '/'),
                "hello",
                sessionId,
                "standard",
                Json: true),
            CancellationToken.None);

        Assert.Equal(0, exit);

        var stdout = outWriter.ToString();
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.True(document.RootElement.TryGetProperty("v", out var version) && version.GetInt32() == 1, line);
            Assert.True(document.RootElement.TryGetProperty("type", out _), line);
        }

        // The human-facing ids and no console control bytes went to stdout.
        Assert.DoesNotContain("session_id:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", stdout, StringComparison.Ordinal);
        Assert.Contains("run_id:", errWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_list_json_is_wrapped_in_the_shared_envelope()
    {
        var handler = new FakeHandler(_ => Json(JsonSerializer.Serialize(new
        {
            status = "ok",
            sessions = Array.Empty<object>(),
        })));

        var outWriter = new StringWriter();
        var commands = new AgentCommands(Session(), new HttpClient(handler), outWriter, new StringWriter());
        var exit = await commands.SessionListAsync(new SessionListCommand(Json: true), CancellationToken.None);

        Assert.Equal(0, exit);
        var line = outWriter.ToString().Trim();
        using var document = JsonDocument.Parse(line);
        Assert.Equal(1, document.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("session_list", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Run_transport_timeout_is_not_reported_as_user_cancellation()
    {
        var runId = "run_" + new string('a', 32);
        var sessionId = new string('b', 32);
        bool cancelPosted = false;

        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                return Json(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    run_id = runId,
                    session_id = sessionId,
                    workspace_id = "ws_" + new string('c', 32),
                    status = "running",
                    preset = "standard",
                    created_at = "2026",
                    updated_at = "2026",
                });
                return Json(json, HttpStatusCode.Created);
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                // What HttpClient raises when its request timeout expires.
                throw new TaskCanceledException("The request timed out.");
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/cancel"))
            {
                cancelPosted = true;
                return Json("{}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error_code\":\"not_found\"}"),
            };
        });

        var errWriter = new StringWriter();
        var commands = new AgentCommands(Session(), new HttpClient(handler), new StringWriter(), errWriter);
        var exit = await commands.RunAsync(
            new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "hello", sessionId, "standard", Json: false),
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(cancelPosted);
        Assert.Contains("stream_timeout", errWriter.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled", errWriter.ToString(), StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _func;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = req => Task.FromResult(func(req));

        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> func) => _func = func;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _func(request);
    }
}
