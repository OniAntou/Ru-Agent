using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatParkReasonWorkflowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatAsync_state_first_park_recovers_only_the_reason_and_does_not_cancel_run(bool includeReason)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('a', 32);
        var payloadRef = "payload_" + new string('e', 32);
        var cancelPosted = false;
        var payloadFetchPosted = false;
        var replayRequested = false;

        string RunJson(string status, long lastSequence, string? terminalAt) => JsonSerializer.Serialize(new
        {
            run_id = runId,
            session_id = "sess_123",
            workspace_id = "ws_test",
            status,
            history_wait_kind = (string?)null,
            preset = "standard",
            policy_snapshot_version = 1,
            policy_snapshot_sha256 = new string('d', 64),
            version = 1L,
            last_sequence = lastSequence,
            created_at = "2026-09-30T00:00:00Z",
            updated_at = "2026-09-30T00:00:00Z",
            terminal_at = terminalAt,
            terminal_error_code = (string?)null,
        });

        // A parked run that already produced model steps carries payload refs, so the
        // turn's follow path reaches RecoverMissingAssistantTextAsync with the token the
        // park already cancelled. That is the branch that must still not cancel the run:
        // without a ref the stream ends cleanly and the guard is never exercised.
        var stepCompleted = JsonSerializer.Serialize(new
        {
            protocol_version = 1,
            event_id = "evt_step_1",
            run_id = runId,
            session_id = "sess_123",
            sequence = 1,
            event_type = "model.step_completed",
            occurred_at = "2026-09-30T00:00:00Z",
            status = "completed",
            safe_summary = "step_done",
            payload_ref = payloadRef,
        });

        var parked = JsonSerializer.Serialize(new
        {
            protocol_version = 1,
            event_id = "evt_park_1",
            run_id = runId,
            session_id = "sess_123",
            sequence = 2,
            event_type = "continue.requested",
            occurred_at = "2026-09-30T00:00:00Z",
            status = "waiting_user",
            safe_summary = "model_step_budget_exhausted",
        });

        var state = JsonSerializer.Serialize(new
        {
            protocol_version = 1, event_id = "evt_state_1", run_id = runId, session_id = "sess_123",
            sequence = 2, event_type = "run.state_changed", occurred_at = "2026-09-30T00:00:00Z",
            status = "waiting_user", safe_summary = "waiting_user",
        });
        var reason = JsonSerializer.Serialize(new
        {
            protocol_version = 1, event_id = "evt_reason_1", run_id = runId, session_id = "sess_123",
            sequence = 3, event_type = "continue.requested", occurred_at = "2026-09-30T00:00:00Z",
            status = "waiting_user", safe_summary = "provider_rate_limited",
        });
        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains("live=0"))
            {
                Assert.Contains("after_sequence=2", uri);
                replayRequested = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(includeReason ? $"id: 3\ndata: {reason}\n\n" : "", Encoding.UTF8, "text/event-stream"),
                };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = "sess_123", preset = "standard", version = 1 }), Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs") && !uri.EndsWith("/cancel"))
            {
                var created = JsonSerializer.Serialize(new
                {
                    run_id = runId,
                    session_id = "sess_123",
                    workspace_id = "ws_test",
                    status = "running",
                    preset = "standard",
                    created_at = DateTimeOffset.UtcNow.ToString("O"),
                    updated_at = DateTimeOffset.UtcNow.ToString("O"),
                });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(created, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/cancel"))
            {
                cancelPosted = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"cancelled\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/payloads/"))
            {
                payloadFetchPosted = true;
                // The response headers arrive, then the body read blocks: the park already
                // cancelled the turn token, so the read surfaces the OCE that the parked
                // guard must survive. A pre-serialized body would complete synchronously
                // and never exercise that branch.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ParkedStream(Array.Empty<byte>()))
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                    },
                };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                var sse = $"id: 1\ndata: {stepCompleted}\n\nid: 2\ndata: {state}\n\nid: 3\ndata: {reason}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ParkedStream(Encoding.UTF8.GetBytes(sse)))
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") },
                    },
                };
            }
            if (req.Method == HttpMethod.Get && uri.EndsWith(runId, StringComparison.Ordinal))
            {
                // The parked run stays non-terminal, so the turn can only end because the
                // stream was released -- not because the run finished on its own.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(RunJson("waiting_user", 2, null), Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var commands = new AgentCommands(session, new HttpClient(handler), outWriter, errWriter, new StringReader("hello\n/exit\n"));

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(replayRequested);
        var combined = Tui.TuiText.Strip(outWriter.ToString() + errWriter.ToString());
        if (includeReason) Assert.Contains("provider_rate_limited", combined);
        else Assert.DoesNotContain("provider_rate_limited", combined);
        Assert.DoesNotContain("waiting_user · resume", combined);
        Assert.False(cancelPosted, "a parked run is the user's to resume; the turn must not cancel it");
        var output = outWriter.ToString();
        Assert.Contains("Continue", output, StringComparison.Ordinal);
        // The payload-ref recovery is what forces the OCE-parked path: the fetch is
        // dispatched and the body read observes the park's cancelled token.
        Assert.True(payloadFetchPosted, "the model.step_completed payload ref must reach recovery");
        // The transcript notice is the only parked guidance: the OCE-parked path must not
        // print a second, near-duplicate line.
        Assert.Equal(1, output.Split("run parked", StringSplitOptions.None).Length - 1);
    }
    private sealed class ParkedStream : System.IO.Stream
    {
        private readonly MemoryStream _preface;

        public ParkedStream(byte[] data) => _preface = new MemoryStream(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _preface.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_preface.Position < _preface.Length)
            {
                return _preface.Read(buffer, offset, count);
            }

            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_preface.Position < _preface.Length)
            {
                return await _preface.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class FakeHandler : HttpMessageHandler    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = req => Task.FromResult(func(req));
        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _func(request);
    }
}
