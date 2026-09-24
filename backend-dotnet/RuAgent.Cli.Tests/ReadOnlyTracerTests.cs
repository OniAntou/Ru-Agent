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
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
                return JsonResponse(JsonSerializer.Serialize(new { session_id = generatedSessionId, preset = "standard", version = 1 }));
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
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
                return JsonResponse(JsonSerializer.Serialize(new { session_id = generatedSessionId, preset = "standard", version = 1 }));
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
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
                return JsonResponse(JsonSerializer.Serialize(new { session_id = generatedSessionId, preset = "standard", version = 1 }));
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
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
                return JsonResponse(JsonSerializer.Serialize(new { session_id = generatedSessionId, preset = "standard", version = 1 }));
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
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
                return JsonResponse(JsonSerializer.Serialize(new { session_id = existingSessionId, preset = "standard", version = 1 }));
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

    [Fact]
    public async Task RunAsync_UpdatesSessionPresetBeforeCreateRun()
    {
        var sessionId = new string('b', 32);
        var runId = ValidRunId;
        var calls = new List<string>();
        long expectedVersion = -1;
        long requestVersion = -1;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/sessions/new"))
                return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId }));
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                calls.Add("preset");
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                expectedVersion = document.RootElement.GetProperty("expected_version").GetInt64();
                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                calls.Add("run");
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                var versionElement = document.RootElement.GetProperty("preset_selection_version");
                requestVersion = versionElement.ValueKind == JsonValueKind.Number
                    ? versionElement.GetInt64()
                    : -1;
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "running", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O") }), HttpStatusCode.Created);
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
                return JsonResponse("id: 1\ndata: {\"protocol_version\":1,\"event_id\":\"evt_test\",\"run_id\":\"" + runId + "\",\"session_id\":\"" + sessionId + "\",\"sequence\":1,\"event_type\":\"run.completed\",\"occurred_at\":\"2026-09-24T00:00:00Z\",\"status\":\"succeeded\",\"safe_summary\":\"done\"}\n\n");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), outWriter, new StringWriter());
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "preset order", null, "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(new[] { "preset", "run" }, calls);
        Assert.Equal(0, expectedVersion);
        Assert.Equal(1, requestVersion);
    }

    [Fact]
    public async Task RunAsync_SuppliesClientSnapshotTokenBeforePresetMutation()
    {
        var sessionId = new string('5', 32);
        var runId = ValidRunId;
        string? token = null;
        var runCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                token = document.RootElement.GetProperty("snapshot_token").GetString();
                Assert.Matches("^sps_[0-9a-f]{32}$", token);
                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1, snapshot_token = token }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                runCalled = true;
                Assert.NotNull(token);
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "running", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O") }), HttpStatusCode.Created);
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
                return JsonResponse("id: 1\ndata: {\"protocol_version\":1,\"event_id\":\"evt_test\",\"run_id\":\"" + runId + "\",\"session_id\":\"" + sessionId + "\",\"sequence\":1,\"event_type\":\"run.completed\",\"occurred_at\":\"2026-09-24T00:00:00Z\",\"status\":\"succeeded\",\"safe_summary\":\"done\"}\n\n");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter());
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "client token", sessionId, "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(runCalled);
    }

    [Fact]
    public async Task RunAsync_MalformedPresetResponseRestoresActualSelectionAndDoesNotCreateRun()
    {
        var sessionId = new string('6', 32);
        string? clientToken = null;
        var state = "standard";
        var restoreCalled = false;
        var runCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    Assert.Equal(clientToken, document.RootElement.GetProperty("snapshot_token").GetString());
                    state = "standard";
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = state, version = 1, restored = true, replayed = false }));
                }

                clientToken = document.RootElement.GetProperty("snapshot_token").GetString();
                state = "full_access";
                return JsonResponse("{}");
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                runCalled = true;
                return JsonResponse("{}", HttpStatusCode.Created);
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter());
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "malformed", sessionId, "full_access", false, ConfirmFullAccess: true), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(restoreCalled);
        Assert.Equal("standard", state);
        Assert.False(runCalled);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("preset")]
    [InlineData("status")]
    public async Task RunAsync_InvalidCreateResponseRestoresBeforeStreaming(string invalidField)
    {
        var sessionId = new string('9', 32);
        var runId = ValidRunId;
        var state = "plan";
        var restoreCalled = false;
        var eventsCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    state = "plan";
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = "standard", version = 1, restored = true, replayed = false }));
                }

                state = "standard";
                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                var responseSession = invalidField == "session" ? new string('b', 32) : sessionId;
                var responsePreset = invalidField == "preset" ? "plan" : "standard";
                var responseStatus = invalidField == "status" ? "failed" : "running";
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = responseSession, workspace_id = "ws_test", status = responseStatus, preset = responsePreset, created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O") }), HttpStatusCode.Created);
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsCalled = true;
                return JsonResponse("id: 1\ndata: {}\n\n");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter());

        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "invalid create", sessionId, "standard", false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(restoreCalled);
        Assert.Equal("plan", state);
        Assert.False(eventsCalled);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("token")]
    public async Task RunAsync_PresetResponseMustBeCompleteAndTokenBound(string responseMode)
    {
        var sessionId = new string('a', 32);
        var state = "plan";
        var restoreCalled = false;
        var runCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    state = "plan";
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = "standard", version = 1, restored = true, replayed = false }));
                }

                state = "full_access";
                return responseMode == "missing"
                    ? JsonResponse("{\"version\":1}")
                    : JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "full_access", version = 1, snapshot_token = "sps_" + new string('d', 32) }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                runCalled = true;
                return JsonResponse("{}", HttpStatusCode.Created);
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter());

        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "incomplete preset", sessionId, "full_access", false, ConfirmFullAccess: true), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(restoreCalled);
        Assert.Equal("plan", state);
        Assert.False(runCalled);
    }

    [Fact]
    public async Task RunAsync_OutputFailureAfterCreateRestoresBeforeReturning()
    {
        var sessionId = new string('b', 32);
        var state = "plan";
        var restoreCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    state = "plan";
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = "standard", version = 1, restored = true, replayed = false }));
                }

                state = "standard";
                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
                return JsonResponse(JsonSerializer.Serialize(new { run_id = ValidRunId, session_id = sessionId, workspace_id = "ws_test", status = "running", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O") }), HttpStatusCode.Created);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var output = new ThrowOnWriteWriter();
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), output, new StringWriter());

        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "output failure", sessionId, "standard", false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(restoreCalled);
        Assert.Equal("plan", state);
    }

    [Fact]
    public async Task RunAsync_PreCommitPresetFailureDoesNotReportRestoreFailure()
    {
        var restoreCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    return JsonResponse("{\"status\":\"ok\",\"restored\":true}", HttpStatusCode.OK);
                }

                return JsonResponse("{\"error_code\":\"preset_snapshot_capacity_exceeded\"}", HttpStatusCode.ServiceUnavailable);
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
                return JsonResponse("{}", HttpStatusCode.Created);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var errWriter = new StringWriter();
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), errWriter);

        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "precommit failure", new string('c', 32), "standard", false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(restoreCalled);
        Assert.Contains("preset_snapshot_capacity_exceeded", errWriter.ToString());
        Assert.DoesNotContain("preset_restore_failed", errWriter.ToString());
    }

    [Theory]
    [InlineData(400, "invalid_request")]
    [InlineData(401, "")]
    [InlineData(403, "")]
    [InlineData(404, "session_not_found")]
    [InlineData(409, "preset_snapshot_token_conflict")]
    [InlineData(413, "")]
    [InlineData(503, "preset_snapshot_capacity_exceeded")]
    public async Task RunAsync_DeterministicPresetErrorDoesNotRestore(int statusCode, string errorCode)
    {
        var sessionId = new string('3', 32);
        var restoreCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = "plan", version = 1, restored = true, replayed = false }));
                }

                var body = string.IsNullOrEmpty(errorCode)
                    ? "{}"
                    : JsonSerializer.Serialize(new { error_code = errorCode });
                return JsonResponse(body, (HttpStatusCode)statusCode);
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter());

        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "deterministic error", sessionId, "standard", false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(restoreCalled);
    }

    [Theory]
    [InlineData(400, "mystery")]
    [InlineData(409, "mystery")]
    [InlineData(500, "preset_version_conflict")]
    public async Task RunAsync_AmbiguousPresetErrorRestoresWithoutVersionRetry(int statusCode, string errorCode)
    {
        var sessionId = new string('2', 32);
        var mutationCalls = 0;
        var restoreCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = "plan", version = 1, restored = true, replayed = false }));
                }

                mutationCalls++;
                return JsonResponse(JsonSerializer.Serialize(new { error_code = errorCode }), (HttpStatusCode)statusCode);
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter());

        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "ambiguous error", sessionId, "standard", false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(1, mutationCalls);
        Assert.True(restoreCalled);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    public async Task RunAsync_AmbiguousCommitErrorRestoresUsingClientToken(int statusCode)
    {
        var sessionId = new string('6', 32);
        var state = "plan";
        var restoreCalled = false;
        var runCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    state = "plan";
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = "plan", version = 1, restored = true, replayed = false }));
                }

                state = "standard";
                return JsonResponse("{\"error_code\":\"storage_commit_unknown\"}", (HttpStatusCode)statusCode);
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                runCalled = true;
                return JsonResponse("{}", HttpStatusCode.Created);
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter());

        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "ambiguous commit", sessionId, "standard", false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(restoreCalled);
        Assert.Equal("plan", state);
        Assert.False(runCalled);
    }

    [Fact]
    public async Task RunAsync_RetriesPresetVersionConflictWithinBound()
    {
        var sessionId = new string('c', 32);
        var runId = ValidRunId;
        var versions = new List<long>();
        long requestVersion = -1;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                var version = document.RootElement.GetProperty("expected_version").GetInt64();
                versions.Add(version);
                if (versions.Count < 4)
                    return JsonResponse("{\"error_code\":\"preset_version_conflict\"}", HttpStatusCode.Conflict);
                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 4 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                var versionElement = document.RootElement.GetProperty("preset_selection_version");
                requestVersion = versionElement.ValueKind == JsonValueKind.Number
                    ? versionElement.GetInt64()
                    : -1;
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "running", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O") }), HttpStatusCode.Created);
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
                return JsonResponse("id: 1\ndata: {\"protocol_version\":1,\"event_id\":\"evt_test\",\"run_id\":\"" + runId + "\",\"session_id\":\"" + sessionId + "\",\"sequence\":1,\"event_type\":\"run.completed\",\"occurred_at\":\"2026-09-24T00:00:00Z\",\"status\":\"succeeded\",\"safe_summary\":\"done\"}\n\n");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter());
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "preset retry", sessionId, "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(new long[] { 0, 1, 2, 3 }, versions);
        Assert.Equal(4, requestVersion);
    }

    [Fact]
    public async Task RunAsync_ReturnsStablePresetConflictAfterBoundedRetry()
    {
        var sessionId = new string('7', 32);
        var versions = new List<long>();
        var createCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                versions.Add(document.RootElement.GetProperty("expected_version").GetInt64());
                return JsonResponse("{\"error_code\":\"preset_version_conflict\"}", HttpStatusCode.Conflict);
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                createCalled = true;
                return JsonResponse("{}", HttpStatusCode.Created);
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var errWriter = new StringWriter();
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), errWriter);
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "preset conflict", sessionId, "standard", false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(new long[] { 0, 1, 2, 3 }, versions);
        Assert.False(createCalled);
        Assert.Contains("preset_version_conflict", errWriter.ToString());
    }

    [Fact]
    public async Task RunAsync_WithoutTrustFlagFailsClosedWithoutReadingInput()
    {
        var sessionId = new string('d', 32);
        var runId = ValidRunId;
        var trustCalled = false;
        var restoreCalled = false;
        var state = "plan";
        var reader = new TrackingReader();
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    state = "plan";
                    Assert.Equal("sps_" + new string('a', 32), document.RootElement.GetProperty("snapshot_token").GetString());
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = (string?)null, version = 0, restored = true, replayed = false }));
                }
                state = "standard";
                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "waiting_workspace_trust", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O"), trust_request_id = "tr_test", display_identity_hash = new string('a', 64) }), HttpStatusCode.Created);
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/trust"))
            {
                trustCalled = true;
                return JsonResponse("{}");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), outWriter, errWriter, reader);
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "trust required", sessionId, "standard", false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(trustCalled);
        Assert.True(restoreCalled);
        Assert.Equal("plan", state);
        Assert.Equal(0, reader.ReadCount);
        Assert.Contains("workspace_trust_required", outWriter.ToString() + errWriter.ToString());
        Assert.DoesNotContain("Trust this workspace?", outWriter.ToString());
    }

    [Fact]
    public async Task RunAsync_WithTrustFlagApprovesSameRun()
    {
        var sessionId = new string('e', 32);
        var runId = ValidRunId;
        var trustRequestId = "tr_test";
        var displayHash = new string('b', 64);
        var createCalls = 0;
        var trustCalls = 0;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1 }));
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
            {
                createCalls++;
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "waiting_workspace_trust", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O"), trust_request_id = trustRequestId, display_identity_hash = displayHash }), HttpStatusCode.Created);
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/trust"))
            {
                trustCalls++;
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                Assert.Equal("approve", document.RootElement.GetProperty("decision").GetString());
                Assert.Equal(trustRequestId, document.RootElement.GetProperty("trust_request_id").GetString());
                Assert.Equal(displayHash, document.RootElement.GetProperty("display_identity_hash").GetString());
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, trust_request_id = trustRequestId, status = "WAITING_HISTORY" }));
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
                return JsonResponse("id: 1\ndata: {\"protocol_version\":1,\"event_id\":\"evt_test\",\"run_id\":\"" + runId + "\",\"session_id\":\"" + sessionId + "\",\"sequence\":1,\"event_type\":\"run.completed\",\"occurred_at\":\"2026-09-24T00:00:00Z\",\"status\":\"succeeded\",\"safe_summary\":\"done\"}\n\n");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), outWriter, new StringWriter(), new StringReader(string.Empty));
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "trust explicit", sessionId, "standard", false, TrustWorkspace: true), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(1, createCalls);
        Assert.Equal(1, trustCalls);
        Assert.DoesNotContain("Trust this workspace?", outWriter.ToString());
    }

    [Fact]
    public async Task RunAsync_LostRestoreResponseRetriesSameTokenAndBlocksStreaming()
    {
        var sessionId = new string('7', 32);
        var runId = ValidRunId;
        var state = "full_access";
        var restoreCalls = 0;
        var eventsCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalls++;
                    if (restoreCalls == 1)
                    {
                        state = "standard";
                        return JsonResponse("{\"error_code\":\"transport_lost\"}", HttpStatusCode.ServiceUnavailable);
                    }

                    Assert.Equal("standard", state);
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = state, version = 1, restored = true, replayed = true }));
                }

                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "full_access", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "waiting_workspace_trust", preset = "full_access", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O"), trust_request_id = "tr_test", display_identity_hash = new string('f', 64) }), HttpStatusCode.Created);
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsCalled = true;
                return JsonResponse("id: 1\ndata: {}\n\n");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var errWriter = new StringWriter();
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), errWriter);
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "restore retry", sessionId, "full_access", false, ConfirmFullAccess: true), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(2, restoreCalls);
        Assert.Equal("standard", state);
        Assert.Contains("preset_restore_retry", errWriter.ToString());
        Assert.False(eventsCalled);
    }

    [Fact]
    public async Task RunAsync_RestoreFailureRetriesThenFailsClosedWithoutStreaming()
    {
        var sessionId = new string('8', 32);
        var runId = ValidRunId;
        var restoreCalls = 0;
        var eventsCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalls++;
                    return JsonResponse("{\"error_code\":\"restore_unavailable\"}", HttpStatusCode.ServiceUnavailable);
                }

                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "full_access", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "waiting_workspace_trust", preset = "full_access", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O"), trust_request_id = "tr_test", display_identity_hash = new string('a', 64) }), HttpStatusCode.Created);
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsCalled = true;
                return JsonResponse("id: 1\ndata: {}\n\n");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var errWriter = new StringWriter();
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), errWriter);

        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "restore unavailable", sessionId, "full_access", false, ConfirmFullAccess: true), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(2, restoreCalls);
        Assert.False(eventsCalled);
        Assert.Contains("preset_restore_failed", errWriter.ToString());
    }

    [Theory]
    [InlineData("invalid_preset")]
    [InlineData("missing_fields")]
    [InlineData("malformed_types")]
    [InlineData("retry_invalid_preset")]
    [InlineData("retry_missing_fields")]
    public async Task RunAsync_RejectsMalformedRestoreProof(string mode)
    {
        var sessionId = new string('5', 32);
        var runId = ValidRunId;
        var restoreCalls = 0;
        var eventsCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalls++;
                    if (mode.StartsWith("retry_", StringComparison.Ordinal) && restoreCalls == 1)
                        return JsonResponse("{\"error_code\":\"transport_lost\"}", HttpStatusCode.ServiceUnavailable);

                    var restoreBody = mode switch
                    {
                        "invalid_preset" => $"{{\"status\":\"ok\",\"session_id\":\"{sessionId}\",\"preset\":\"invalid\",\"version\":999,\"restored\":true,\"replayed\":false}}",
                        "missing_fields" => $"{{\"status\":\"ok\",\"session_id\":\"{sessionId}\",\"restored\":true}}",
                        "malformed_types" => JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = 1, version = "1", restored = true, replayed = false }),
                        "retry_invalid_preset" => $"{{\"status\":\"ok\",\"session_id\":\"{sessionId}\",\"preset\":\"invalid\",\"version\":999,\"restored\":true,\"replayed\":true}}",
                        _ => $"{{\"status\":\"ok\",\"session_id\":\"{sessionId}\",\"restored\":true}}",
                    };
                    return JsonResponse(restoreBody);
                }

                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "waiting_workspace_trust", preset = "standard", created_at = "2026-09-24T00:00:00Z", updated_at = "2026-09-24T00:00:00Z", trust_request_id = "trust_test", display_identity_hash = new string('a', 64) }), HttpStatusCode.Created);
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsCalled = true;
                return JsonResponse("id: 1\ndata: {}\n\n");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var errWriter = new StringWriter();
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), errWriter);

        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "restore proof", sessionId, "standard", false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(mode.StartsWith("retry_", StringComparison.Ordinal) ? 2 : 1, restoreCalls);
        Assert.Contains("preset_restore_failed", errWriter.ToString());
        Assert.False(eventsCalled);
    }

    [Fact]
    public async Task RunAsync_MalformedCommitWithRestoreNotFoundFailsClosed()
    {
        var sessionId = new string('1', 32);
        var restoreCalled = false;
        var eventsCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    return JsonResponse("{\"error_code\":\"preset_snapshot_not_found\"}", HttpStatusCode.Conflict);
                }

                return JsonResponse("{}", HttpStatusCode.OK);
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsCalled = true;
                return JsonResponse("id: 1\ndata: {}\n\n");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var errWriter = new StringWriter();
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), errWriter);

        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "malformed commit", sessionId, "standard", false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(restoreCalled);
        Assert.Contains("preset_restore_failed", errWriter.ToString());
        Assert.False(eventsCalled);
    }

    [Fact]
    public async Task RunAsync_TrustResponseMismatchFailsClosed()
    {
        var sessionId = new string('f', 32);
        var runId = ValidRunId;
        var eventsCalled = false;
        var restoreCalled = false;
        var state = "plan";
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    state = "plan";
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = (string?)null, version = 0, restored = true, replayed = false }));
                }
                state = "standard";
                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "waiting_workspace_trust", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O"), trust_request_id = "tr_expected", display_identity_hash = new string('c', 64) }), HttpStatusCode.Created);
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/trust"))
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, trust_request_id = "tr_other", status = "waiting_history" }));
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsCalled = true;
                return JsonResponse("id: 1\ndata: {}\n\n");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter(), new StringReader(string.Empty));
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "trust mismatch", sessionId, "standard", false, TrustWorkspace: true), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(eventsCalled);
        Assert.True(restoreCalled);
        Assert.Equal("plan", state);
    }

    [Fact]
    public async Task RunAsync_TrustApiFailureRestoresPreset()
    {
        var sessionId = new string('6', 32);
        var runId = ValidRunId;
        var restoreCalled = false;
        var eventsCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = (string?)null, version = 0, restored = true, replayed = false }));
                }
                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "waiting_workspace_trust", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O"), trust_request_id = "tr_test", display_identity_hash = new string('d', 64) }), HttpStatusCode.Created);
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/trust"))
                return JsonResponse("{\"error_code\":\"trust_failed\"}", HttpStatusCode.Conflict);
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsCalled = true;
                return JsonResponse("id: 1\\ndata: {}\\n\\n");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter(), new StringReader(string.Empty));
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "trust failure", sessionId, "standard", false, TrustWorkspace: true), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(restoreCalled);
        Assert.False(eventsCalled);
    }

    [Fact]
    public async Task RunAsync_ConfirmedFullAccessTrustFailureRestoresPreset()
    {
        var sessionId = new string('5', 32);
        var runId = ValidRunId;
        var restoreCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = (string?)null, version = 0, restored = true, replayed = false }));
                }
                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "full_access", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "waiting_workspace_trust", preset = "full_access", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O"), trust_request_id = "tr_test", display_identity_hash = new string('e', 64) }), HttpStatusCode.Created);
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/trust"))
                return JsonResponse("{\"error_code\":\"trust_failed\"}", HttpStatusCode.Conflict);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter(), new StringReader(string.Empty));
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "full trust failure", sessionId, "full_access", false, TrustWorkspace: true, ConfirmFullAccess: true), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(restoreCalled);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("created")]
    [InlineData("")]
    [InlineData(null)]
    public async Task RunAsync_UnknownTrustStatusRestoresPreset(string? trustStatus)
    {
        var sessionId = new string('4', 32);
        var runId = ValidRunId;
        var restoreCalled = false;
        var eventsCalled = false;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    return JsonResponse(JsonSerializer.Serialize(new { status = "ok", session_id = sessionId, preset = (string?)null, version = 0, restored = true, replayed = false }));
                }
                return JsonResponse(JsonSerializer.Serialize(new { session_id = sessionId, preset = "standard", version = 1 }));
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/runs"))
                return JsonResponse(JsonSerializer.Serialize(new { run_id = runId, session_id = sessionId, workspace_id = "ws_test", status = "waiting_workspace_trust", preset = "standard", created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O"), trust_request_id = "tr_test", display_identity_hash = new string('f', 64) }), HttpStatusCode.Created);
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/trust"))
                return JsonResponse(trustStatus is null
                    ? JsonSerializer.Serialize(new { run_id = runId, trust_request_id = "tr_test" })
                    : JsonSerializer.Serialize(new { run_id = runId, trust_request_id = "tr_test", status = trustStatus }));
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsCalled = true;
                return JsonResponse("id: 1\\ndata: {}\\n\\n");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter(), new StringReader(string.Empty));
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "trust unknown", sessionId, "standard", false, TrustWorkspace: true), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(restoreCalled);
        Assert.False(eventsCalled);
    }

    [Fact]
    public async Task RunAsync_FullAccessRequiresConfirmationBeforeHttp()
    {
        var calls = 0;
        var handler = new FakeHandler(_ =>
        {
            calls++;
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });
        var commands = new AgentCommands(CreateSession(), new HttpClient(handler), new StringWriter(), new StringWriter());
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath().TrimEnd('\\', '/'), "full access", new string('1', 32), "full_access", false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(0, calls);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ThrowOnWriteWriter : StringWriter
    {
        public override Task WriteLineAsync(string? value) => throw new IOException("output failed");
    }

    private sealed class TrackingReader : StringReader
    {
        public TrackingReader() : base(string.Empty) { }
        public int ReadCount { get; private set; }
        public override string? ReadLine()
        {
            ReadCount++;
            return base.ReadLine();
        }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return base.ReadLineAsync(cancellationToken);
        }
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
    public void JsonRequiresExplicitFullAccessConfirmation()
    {
        var runJson = AgentCliParser.Parse(new[] { "agent", "run", "--workspace", Path.GetTempPath(), "--preset", "full_access", "--confirm-full-access", "--json", "prompt" });
        Assert.True(runJson.Success);

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
