using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ParkRecoveryRegressionTests
{
    private static readonly string RunId = "run_" + new string('a', 32);
    private const string SessionId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData("waiting_user")]
    [InlineData("reconciliation_required")]
    [InlineData("unsafe\nnotice\u001b[2J")]
    public void Park_detection_never_reprints_an_unbounded_or_control_bearing_reason(string summary)
    {
        var envelope = Envelope(1, "continue.requested", "waiting_user", summary);
        var frame = new SseFrame(1, "", JsonSerializer.Serialize(envelope), false, null);
        Assert.True(RunParkSignal.TryRead(frame, RunId, out var reason));
        Assert.Null(reason);
        Assert.False(RunParkSignal.TryRead(frame with { IsTransient = true }, RunId, out _));
        Assert.False(RunParkSignal.TryRead(frame with { DurableSequence = 2 }, RunId, out _));
        Assert.False(RunParkSignal.TryRead(frame, "run_other", out _));
    }

    [Theory]
    [InlineData(false, "waiting_user", "continue.requested")]
    [InlineData(true, "waiting_user", "continue.requested")]
    [InlineData(false, "reconciliation_required", "reconciliation.required")]
    [InlineData(true, "reconciliation_required", "reconciliation.required")]
    public async Task Attach_returns_parked_exit_without_waiting_for_live_feed_or_cancelling(bool json, string status, string eventType)
    {
        var posted = false;
        var stream = new HeldStream(Frame(1, eventType, status, "provider_quota_exhausted"));
        using var http = new HttpClient(new Handler(req =>
        {
            if (req.Method == HttpMethod.Post) { posted = true; throw new InvalidOperationException("unexpected POST"); }
            return req.RequestUri!.AbsolutePath.EndsWith("/events")
                ? Sse(stream) : Json(RunJson(status, 1));
        }));
        var output = new StringWriter();
        var error = new StringWriter();
        using var commands = new AgentCommands(Session(), http, output, error);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var exit = await commands.AttachAsync(new AttachCommand(RunId, json), deadline.Token);

        Assert.Equal(2, exit);
        Assert.False(posted);
        Assert.True(stream.Disposed);
        Assert.False(deadline.IsCancellationRequested);
        var notices = json ? error.ToString() : output.ToString();
        Assert.Contains("provider_quota_exhausted", notices);
        Assert.Contains("cancel", notices);
        if (json)
        {
            foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(line);
                Assert.Equal("event", document.RootElement.GetProperty("type").GetString());
            }
            Assert.DoesNotContain("run parked", output.ToString());
        }
    }

    [Fact]
    public async Task Replaying_an_old_park_does_not_detach_a_resumed_run()
    {
        var events = Frame(1, "continue.requested", "waiting_user", "provider_rate_limited")
            + Frame(2, "continue.requested", "running", "continued")
            + Frame(3, "run.state_changed", "completed", "completed");
        using var http = new HttpClient(new Handler(req => req.RequestUri!.AbsolutePath.EndsWith("/events")
            ? Sse(new MemoryStream(Encoding.UTF8.GetBytes(events))) : Json(RunJson("completed", 3))));
        var output = new StringWriter();
        using var commands = new AgentCommands(Session(), http, output, new StringWriter());

        Assert.Equal(0, await commands.AttachAsync(new AttachCommand(RunId, false), CancellationToken.None));
        Assert.DoesNotContain("run parked", output.ToString());
        Assert.Contains("completed", output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_returns_parked_exit_after_creation_without_cancelling(bool json)
    {
        var cancelPosts = 0;
        using var http = new HttpClient(new Handler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/cancel")) { cancelPosts++; return Json("{}"); }
            if (path.EndsWith("/settings")) return Json("{}");
            if (path.EndsWith("/policies")) return Json(JsonSerializer.Serialize(new { session_id = SessionId, preset = "standard", version = 1 }));
            if (req.Method == HttpMethod.Post && path.EndsWith("/runs"))
            {
                var created = Json(RunJson("running", 0));
                created.StatusCode = HttpStatusCode.Created;
                return created;
            }
            if (path.EndsWith("/events")) return Sse(new HeldStream(Frame(1, "continue.requested", "waiting_user", "provider_rate_limited")));
            return Json(RunJson("waiting_user", 1));
        }));
        var output = new StringWriter();
        var error = new StringWriter();
        using var commands = new AgentCommands(Session(), http, output, error);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exit = await commands.RunAsync(new RunCommand(Path.GetTempPath(), "hello", SessionId, "standard", json), deadline.Token);
        Assert.Equal(2, exit);
        Assert.Equal(0, cancelPosts);
        Assert.Contains("run parked", (json ? error : output).ToString());
        if (json) foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal("event", document.RootElement.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Chat_release_allows_the_next_prompt_to_create_a_fresh_run()
    {
        var nextRun = "run_" + new string('c', 32);
        var createdRuns = 0;
        var cancelled = false;
        using var http = new HttpClient(new Handler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/settings")) return Json("{}");
            if (req.Method == HttpMethod.Post && path.EndsWith("/policies"))
                return Json(JsonSerializer.Serialize(new { session_id = SessionId, preset = "standard", version = 1 }));
            if (req.Method == HttpMethod.Post && path.EndsWith("/runs"))
            {
                createdRuns++;
                if (createdRuns == 2) Assert.True(cancelled, "release must finish before the next create");
                var created = Json(RunJson("running", 0, runId: createdRuns == 1 ? RunId : nextRun));
                created.StatusCode = HttpStatusCode.Created;
                return created;
            }
            if (req.Method == HttpMethod.Post && path == $"/agent/v1/runs/{RunId}/cancel")
            { cancelled = true; return Json("{\"status\":\"cancelled\"}"); }
            if (path.EndsWith("/events"))
            {
                if (path.Contains(nextRun))
                {
                    var done = Envelope(1, "run.state_changed", "completed", "completed") with { RunId = nextRun };
                    return Sse(new MemoryStream(Encoding.UTF8.GetBytes($"id: 1\ndata: {JsonSerializer.Serialize(done)}\n\n")));
                }
                return Sse(new HeldStream(Frame(1, "continue.requested", "waiting_user", "provider_quota_exhausted")));
            }
            if (path.EndsWith(nextRun)) return Json(RunJson("completed", 1, runId: nextRun));
            if (path.EndsWith(RunId)) return Json(RunJson(cancelled ? "cancelled" : "waiting_user", 1));
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }));
        var output = new StringWriter();
        using var commands = new AgentCommands(Session(), http, output, new StringWriter(), new StringReader("first\n/release\nsecond\n/exit\n"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exit = await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), SessionId, "standard", false), deadline.Token);
        Assert.Equal(0, exit);
        Assert.True(cancelled);
        Assert.Equal(2, createdRuns);
        Assert.Contains("run released", TuiText.Strip(output.ToString()));
    }

    [Theory]
    [InlineData("waiting_user", false, false, false)]
    [InlineData("reconciliation_required", false, false, false)]
    [InlineData("completed", false, false, false)]
    [InlineData("running", false, false, true)]
    [InlineData("waiting_user", true, false, true)]
    [InlineData("waiting_user", false, true, true)]
    public async Task Release_checks_ownership_and_only_forgets_identity_after_success(
        string status, bool otherSession, bool cancelFails, bool expectedError)
    {
        var posts = 0;
        using var owner = new ChatRunSession();
        owner.AcceptCreatedRun(RunId);
        owner.RememberParked(RunId);
        using var http = new HttpClient(new Handler(req =>
        {
            if (req.Method == HttpMethod.Get) return Json(RunJson(status, 1, otherSession ? "sess_other" : SessionId));
            Assert.EndsWith("/cancel", req.RequestUri!.AbsolutePath);
            Assert.Equal(RunId, owner.ParkedRunId); // Retained until server confirms.
            posts++;
            return cancelFails ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{}") }
                : Json("{\"status\":\"cancelled\"}");
        }));
        using var client = new AgentApiClient(Session(), http);

        var result = await ChatRunRelease.ReleaseAsync(client, owner, SessionId, null, CancellationToken.None);

        Assert.Equal(expectedError, result.IsError);
        Assert.Equal(expectedError ? RunId : null, owner.ParkedRunId);
        Assert.Equal(expectedError ? RunId : null, owner.ActiveRunId);
        Assert.Equal(expectedError ? null : RunId, result.ReleasedRunId);
        Assert.Equal(!otherSession && status is "waiting_user" or "reconciliation_required" ? 1 : 0, posts);
    }

    [Fact]
    public async Task Release_keeps_an_unrelated_retained_run_and_propagates_cancellation()
    {
        using var owner = new ChatRunSession();
        owner.RememberParked("run_" + new string('b', 32));
        using var http = new HttpClient(new Handler(req => req.Method == HttpMethod.Get
            ? Json(RunJson("waiting_user", 1)) : Json("{}")));
        using var client = new AgentApiClient(Session(), http);
        var result = await ChatRunRelease.ReleaseAsync(client, owner, SessionId, RunId, CancellationToken.None);
        Assert.False(result.IsError);
        Assert.EndsWith(new string('b', 32), owner.ParkedRunId);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ChatRunRelease.ReleaseAsync(client, owner, SessionId, RunId, cancelled.Token));
        Assert.EndsWith(new string('b', 32), owner.ParkedRunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_repeated_park_after_continue_stops_suggesting_the_same_retry(bool stateFirst)
    {
        var output = new StringWriter();
        var surface = new TuiSurface(output);
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { Width = 160 };
        var park = Envelope(1, "continue.requested", "waiting_user", "provider_rate_limited");
        transcript.ApplyDurable(park);
        transcript.ApplyDurable(park); // Duplicate within one park is not a repeat.
        Assert.DoesNotContain("still blocked", TuiText.Strip(output.ToString()));
        transcript.BeginTurn();
        // A model step may complete without the run reaching a terminal state.
        transcript.ApplyDurable(Envelope(2, "model.step_completed", "completed", "step_done"));
        output.GetStringBuilder().Clear();
        if (stateFirst) transcript.ApplyDurable(Envelope(3, "run.state_changed", "waiting_user", "waiting_user"));
        transcript.ApplyDurable(park with { Sequence = 4 });
        var text = TuiText.Strip(output.ToString());
        Assert.Contains("still blocked after Continue", text);
        Assert.DoesNotContain("wait before /continue", text);
        Assert.Contains("/release", text);

        transcript.ForgetParkHistory(RunId);
        transcript.BeginTurn();
        output.GetStringBuilder().Clear();
        transcript.ApplyDurable(park with { Sequence = 5 });
        Assert.Contains("wait before /continue", TuiText.Strip(output.ToString()));
    }

    [Fact]
    public void Changed_reason_or_finished_run_does_not_reuse_repeat_advice()
    {
        var output = new StringWriter();
        var surface = new TuiSurface(output);
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { Width = 160 };
        transcript.ApplyDurable(Envelope(1, "continue.requested", "waiting_user", "provider_rate_limited"));
        transcript.BeginTurn();
        output.GetStringBuilder().Clear();
        transcript.ApplyDurable(Envelope(2, "continue.requested", "waiting_user", "provider_quota_exhausted"));
        Assert.Contains("restore provider credits", TuiText.Strip(output.ToString()));
        Assert.DoesNotContain("still blocked", TuiText.Strip(output.ToString()));
        transcript.ApplyDurable(Envelope(3, "run.state_changed", "cancelled", "cancelled"));
        transcript.BeginTurn();
        output.GetStringBuilder().Clear();
        transcript.ApplyDurable(Envelope(4, "continue.requested", "waiting_user", "provider_quota_exhausted"));
        Assert.DoesNotContain("still blocked", TuiText.Strip(output.ToString()));
    }

    private static HarnessSafeEventEnvelope Envelope(long sequence, string type, string status, string summary) =>
        new(1, "evt_test", RunId, SessionId, sequence, type, "2026-10-19T00:00:00Z", status, summary, null);

    private static string Frame(long sequence, string type, string status, string summary) =>
        $"id: {sequence}\ndata: {JsonSerializer.Serialize(Envelope(sequence, type, status, summary))}\n\n";

    private static string RunJson(string status, long lastSequence, string sessionId = SessionId, string? runId = null) => JsonSerializer.Serialize(new
    {
        run_id = runId ?? RunId, session_id = sessionId, workspace_id = "ws_test", status, history_wait_kind = (string?)null,
        preset = "standard", policy_snapshot_version = 1, policy_snapshot_sha256 = new string('d', 64), version = 1L,
        last_sequence = lastSequence, created_at = "2026-10-19T00:00:00Z", updated_at = "2026-10-19T00:00:00Z",
        terminal_at = status == "completed" ? "2026-10-19T00:00:00Z" : null, terminal_error_code = (string?)null,
    });

    private static PublicHostSession Session()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return PublicHostSession.FromDescriptor(new RuntimeDescriptor(1, "http://127.0.0.1:8000/", new string('a', 64),
            process.Id, process.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O")));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(Stream stream) => new(HttpStatusCode.OK)
        { Content = new StreamContent(stream) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } } };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(handle(request)); }
    }

    private sealed class HeldStream(string prefix) : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(prefix));
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _prefix.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_prefix.Position < _prefix.Length) return await _prefix.ReadAsync(buffer, token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; if (disposing) _prefix.Dispose(); base.Dispose(disposing); }
    }
}
