using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ReadOnlyTracerTests
{
    private static PublicHostSession CreateSession(string bearer = "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, "http://127.0.0.1:8000/", bearer, proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(desc);
    }

    private static string ValidRunId => "run_" + new string('a', 32);
    private static string ValidSessionId => new string('b', 32);

    [Fact]
    public async Task OmittedSession_CreatesDedicatedSessionAndPrintsIds()
    {
        var session = CreateSession();
        var generatedSessionId = new string('c', 32);
        var runId = ValidRunId;
        var workspace = Path.GetTempPath().TrimEnd('\\', '/');
        var calls = new List<string>();

        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            calls.Add($"{req.Method} {uri}");
            if (req.Method == HttpMethod.Post && uri.EndsWith("/sessions/new"))
            {
                Assert.NotNull(req.Headers.Authorization);
                Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
                var json = JsonSerializer.Serialize(new { status = "ok", session_id = generatedSessionId });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                // Read body to verify preset standard etc handled in next test; just return run creation
                var body = req.Content!.ReadAsStringAsync().Result;
                var doc = JsonDocument.Parse(body);
                // Validate preset present
                Assert.True(doc.RootElement.TryGetProperty("preset", out var preset) && preset.GetString() == "standard");
                var respJson = JsonSerializer.Serialize(new { run_id = runId, session_id = generatedSessionId, workspace_id = "ws_" + new string('d', 32), status = "running", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O") });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                var sse = $"id: 1\ndata: {{\"protocol_version\":1,\"event_id\":\"evt_{new string('e', 32)}\",\"run_id\":\"{runId}\",\"session_id\":\"{generatedSessionId}\",\"sequence\":1,\"event_type\":\"run.state_changed\",\"occurred_at\":\"2026-08-24T00:00:00Z\",\"status\":\"running\",\"safe_summary\":\"started\"}}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error_code\":\"not_found\"}") };
        });

        var httpClient = new HttpClient(handler);
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var commands = new AgentCommands(session, httpClient, outWriter, errWriter);
        var cmd = new RunCommand(workspace, "hello read-only", null, "standard", false);
        var exit = await commands.RunAsync(cmd, CancellationToken.None);

        Assert.Equal(0, exit);
        // Must have called POST /sessions/new exactly once (dedicated session)
        var sessionCalls = calls.Count(c => c.Contains("POST") && c.Contains("/sessions/new"));
        Assert.Equal(1, sessionCalls);

        var output = outWriter.ToString();
        // Must print session/run IDs
        Assert.Contains(generatedSessionId, output);
        Assert.Contains(runId, output);
    }

    [Fact]
    public async Task UsesStandardPresetAndFreshIdempotencyKey()
    {
        var session = CreateSession();
        var generatedSessionId = new string('f', 32);
        var runId = ValidRunId;
        var workspace = Path.GetTempPath().TrimEnd('\\', '/');
        var idempotencyKeys = new List<string>();

        FakeHandler handler = null!;
        handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/sessions/new"))
            {
                var json = JsonSerializer.Serialize(new { status = "ok", session_id = generatedSessionId });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                var doc = JsonDocument.Parse(body);
                Assert.True(doc.RootElement.TryGetProperty("preset", out var p) && p.GetString() == "standard", "Must use Standard preset");
                Assert.True(doc.RootElement.TryGetProperty("idempotency_key", out var k), "Missing idempotency_key");
                var key = k.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(key));
                // Fresh idempotency key must be 32 hex (Guid N)
                Assert.Equal(32, key.Length);
                Assert.Matches("^[0-9a-f]{32}$", key);
                idempotencyKeys.Add(key);
                var respJson = JsonSerializer.Serialize(new { run_id = runId, session_id = generatedSessionId, workspace_id = "ws_" + new string('a', 32), status = "running", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O") });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                var sse = $"id: 1\ndata: {{\"protocol_version\":1,\"event_id\":\"evt_{new string('a', 32)}\",\"run_id\":\"{runId}\",\"session_id\":\"{generatedSessionId}\",\"sequence\":1,\"event_type\":\"run.state_changed\",\"occurred_at\":\"2026-08-24T00:00:00Z\",\"status\":\"running\",\"safe_summary\":\"ok\"}}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var httpClient = new HttpClient(handler);

        // First run
        var out1 = new StringWriter();
        var cmd1 = new RunCommand(workspace, "prompt one", null, "standard", false);
        var commands1 = new AgentCommands(session, httpClient, out1, new StringWriter());
        await commands1.RunAsync(cmd1, CancellationToken.None);

        // Second run with new handler to capture second key freshly (need new commands with same handler list)
        var httpClient2 = new HttpClient(handler);
        var out2 = new StringWriter();
        var commands2 = new AgentCommands(session, httpClient2, out2, new StringWriter());
        var cmd2 = new RunCommand(workspace, "prompt two", null, "standard", false);
        await commands2.RunAsync(cmd2, CancellationToken.None);

        Assert.Equal(2, idempotencyKeys.Count);
        Assert.NotEqual(idempotencyKeys[0], idempotencyKeys[1]);
    }

    [Fact]
    public async Task FollowsSseCursor()
    {
        var session = CreateSession();
        var generatedSessionId = new string('b', 32);
        var runId = ValidRunId;
        var workspace = Path.GetTempPath().TrimEnd('\\', '/');
        var capturedAfterSequence = new List<string>();

        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/sessions/new"))
            {
                var json = JsonSerializer.Serialize(new { status = "ok", session_id = generatedSessionId });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                var respJson = JsonSerializer.Serialize(new { run_id = runId, session_id = generatedSessionId, workspace_id = "ws_" + new string('c', 32), status = "running", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O") });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                capturedAfterSequence.Add(uri);
                // Validate cursor starts at 0 and following via Last-Event-ID not needed for this simple handler
                Assert.Contains("after_sequence=0", uri);
                var sseBody =
                    $"id: 1\ndata: {{\"protocol_version\":1,\"event_id\":\"evt_{new string('a', 32)}\",\"run_id\":\"{runId}\",\"session_id\":\"{generatedSessionId}\",\"sequence\":1,\"event_type\":\"run.state_changed\",\"occurred_at\":\"2026-08-24T00:00:00Z\",\"status\":\"running\",\"safe_summary\":\"first\"}}\n\n" +
                    $"id: 2\ndata: {{\"protocol_version\":1,\"event_id\":\"evt_{new string('b', 32)}\",\"run_id\":\"{runId}\",\"session_id\":\"{generatedSessionId}\",\"sequence\":2,\"event_type\":\"tool.proposed\",\"occurred_at\":\"2026-08-24T00:00:01Z\",\"status\":\"running\",\"safe_summary\":\"proposed tool\"}}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var httpClient = new HttpClient(handler);
        var outWriter = new StringWriter();
        var commands = new AgentCommands(session, httpClient, outWriter, new StringWriter());
        var cmd = new RunCommand(workspace, "prompt cursor", null, "standard", false);
        await commands.RunAsync(cmd, CancellationToken.None);

        Assert.Single(capturedAfterSequence);
        var output = outWriter.ToString();
        // Should have rendered both safe events via SafeEventRenderer
        Assert.Contains("run.state_changed", output);
        Assert.Contains("tool.proposed", output);
        Assert.Contains("[1]", output);
        Assert.Contains("[2]", output);
    }

    [Fact]
    public async Task CtrlC_PostsCancelBeforeExit()
    {
        var session = CreateSession();
        var generatedSessionId = new string('a', 32);
        var runId = ValidRunId;
        var workspace = Path.GetTempPath().TrimEnd('\\', '/');
        bool cancelPosted = false;

        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/sessions/new"))
            {
                var json = JsonSerializer.Serialize(new { status = "ok", session_id = generatedSessionId });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                var respJson = JsonSerializer.Serialize(new { run_id = runId, session_id = generatedSessionId, workspace_id = "ws_" + new string('c', 32), status = "running", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O") });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                // Return a stream that blocks indefinitely until cancelled (like AgentSseClientTests BlockingStream)
                var content = new StreamContent(new BlockingStream());
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/cancel"))
            {
                cancelPosted = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"cancelled\"}", Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var httpClient = new HttpClient(handler);
        var outWriter = new StringWriter();
        var commands = new AgentCommands(session, httpClient, outWriter, new StringWriter());
        var cmd = new RunCommand(workspace, "prompt cancel", null, "standard", false);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(150);

        var exitCode = await commands.RunAsync(cmd, cts.Token);

        Assert.True(cancelPosted, "Ctrl+C must POST cancel before exit");
        // Exit code 130 or 0 with cancellation handling; our RunAsync returns 130 on cancel
        Assert.True(exitCode == 130 || exitCode == 0);
    }

    [Fact]
    public async Task WithExistingSession_DoesNotCreateNewSession()
    {
        var session = CreateSession();
        var existingSessionId = new string('d', 32);
        var runId = ValidRunId;
        var workspace = Path.GetTempPath().TrimEnd('\\', '/');
        int sessionCreates = 0;

        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/sessions/new"))
            {
                sessionCreates++;
                var json = JsonSerializer.Serialize(new { status = "ok", session_id = new string('z', 32) });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                var doc = JsonDocument.Parse(body);
                var sid = doc.RootElement.GetProperty("session_id").GetString();
                Assert.Equal(existingSessionId, sid);
                var respJson = JsonSerializer.Serialize(new { run_id = runId, session_id = existingSessionId, workspace_id = "ws_" + new string('a', 32), status = "running", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O") });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(respJson, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                var sse = $"id: 1\ndata: {{\"protocol_version\":1,\"event_id\":\"evt_{new string('a', 32)}\",\"run_id\":\"{runId}\",\"session_id\":\"{existingSessionId}\",\"sequence\":1,\"event_type\":\"run.state_changed\",\"occurred_at\":\"2026-08-24T00:00:00Z\",\"status\":\"running\",\"safe_summary\":\"ok\"}}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var httpClient = new HttpClient(handler);
        var commands = new AgentCommands(session, httpClient, new StringWriter(), new StringWriter());
        var cmd = new RunCommand(workspace, "prompt with session", existingSessionId, "standard", false);
        await commands.RunAsync(cmd, CancellationToken.None);
        Assert.Equal(0, sessionCreates);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_func(request));
    }

    private sealed class BlockingStream : System.IO.Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Task.Delay(Timeout.Infinite).Wait();
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    [Fact]
    public void JsonCannotBypassInteractiveRequirements()
    {
        // Policy per Task 9: --json cannot confirm Full Access, trust, approval, or global policy import
        var runJson = AgentCliParser.Parse(new[] { "agent", "run", "--workspace", Path.GetTempPath(), "--preset", "full_access", "--json", "prompt" });
        Assert.False(runJson.Success);
        Assert.Equal("interactive_required", runJson.ErrorCode);

        var trustJson = AgentCliParser.Parse(new[] { "agent", "trust", "run_" + new string('a', 32), "--approve", "--json" });
        Assert.False(trustJson.Success);
        Assert.Equal("interactive_required", trustJson.ErrorCode);

        var approveJson = AgentCliParser.Parse(new[] { "agent", "approve", "run_" + new string('a', 32), "apr_" + new string('b', 32), "--json" });
        Assert.False(approveJson.Success);
        Assert.Equal("interactive_required", approveJson.ErrorCode);

        var importJson = AgentCliParser.Parse(new[] { "agent", "policy", "import", "--global", "file.json", "--json" });
        Assert.False(importJson.Success);
        Assert.Equal("interactive_required", importJson.ErrorCode);
    }
}
