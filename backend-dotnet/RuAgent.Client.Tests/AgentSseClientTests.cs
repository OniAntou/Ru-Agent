using System.Net;
using System.Text;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentSseClientTests
{
    private static PublicHostSession Session()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, "http://127.0.0.1:8000/", new string('e', 64), proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(desc);
    }

    [Fact]
    public async Task CursorAndReconnectDurable()
    {
        var runId = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var sseBody =
            "\uFEFFid: 1\n" +
            "data: {\"protocol_version\":1,\"event_id\":\"evt_1\",\"run_id\":\"" + runId + "\",\"session_id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sequence\":1,\"event_type\":\"run.state_changed\",\"occurred_at\":\"2026-08-24T00:00:00Z\",\"status\":\"running\",\"safe_summary\":\"ok\"}\n\n" +
            "id: 2\n" +
            "data: {\"protocol_version\":1,\"event_id\":\"evt_2\",\"run_id\":\"" + runId + "\",\"session_id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sequence\":2,\"event_type\":\"tool.proposed\",\"occurred_at\":\"2026-08-24T00:00:01Z\",\"status\":\"running\",\"safe_summary\":\"proposed\"}\n\n";

        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            // Should contain after_sequence query
            Assert.Contains("after_sequence", uri);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream")
            };
            return resp;
        });

        var client = new AgentSseClient(Session(), new HttpClient(handler));
        var frames = new List<SseFrame>();
        await foreach (var f in client.ReadEventsAsync(runId, afterSequence: 0))
        {
            frames.Add(f);
            if (frames.Count >= 2) break;
        }
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.False(f.IsTransient));
        Assert.Equal(1, frames[0].DurableSequence);
        Assert.Equal(2, frames[1].DurableSequence);
    }

    [Fact]
    public async Task TransientFramesDoNotAdvanceDurableCursor()
    {
        var runId = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var sseBody =
            "id: 1\n" +
            "data: {\"protocol_version\":1,\"event_id\":\"evt_1\",\"run_id\":\"" + runId + "\",\"session_id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sequence\":1,\"event_type\":\"run.state_changed\",\"occurred_at\":\"2026-08-24T00:00:00Z\",\"status\":\"running\",\"safe_summary\":\"ok\"}\n\n" +
            "event: transient\n" +
            "id: t1\n" +
            "data: {\"kind\":\"assistant.delta\",\"data\":\"hello\"}\n\n" +
            "id: 2\n" +
            "data: {\"protocol_version\":1,\"event_id\":\"evt_2\",\"run_id\":\"" + runId + "\",\"session_id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sequence\":2,\"event_type\":\"tool.execution_completed\",\"occurred_at\":\"2026-08-24T00:00:01Z\",\"status\":\"running\",\"safe_summary\":\"done\"}\n\n";
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream") });
        var client = new AgentSseClient(Session(), new HttpClient(handler));
        var frames = new List<SseFrame>();
        await foreach (var f in client.ReadEventsAsync(runId))
            frames.Add(f);

        Assert.Equal(3, frames.Count);
        Assert.False(frames[0].IsTransient);
        Assert.True(frames[1].IsTransient);
        Assert.False(frames[2].IsTransient);
        Assert.Equal(1, frames[1].TransientSequence);
        Assert.Null(frames[1].DurableSequence);
    }

    [Fact]
    public async Task Oversized_single_line_is_rejected_before_unbounded_buffering()
    {
        var runId = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var sseBody = "id: 1\n" + "data: " + new string('x', 512 * 1024) + "\n\n";
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream"),
        });
        var client = new AgentSseClient(Session(), new HttpClient(handler));

        var error = await Assert.ThrowsAsync<AgentApiException>(async () =>
        {
            await foreach (var _ in client.ReadEventsAsync(runId))
            {
            }
        });

        Assert.Equal("sse_frame_too_large", error.ErrorCode);
    }

    [Fact]
    public async Task CancellationStopsStream()
    {
        var runId = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") } } });
        var client = new AgentSseClient(Session(), new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.ReadEventsAsync(runId, cancellationToken: cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Supplied_http_client_is_not_reconfigured_or_disposed()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(2),
        };

        var client = new AgentSseClient(Session(), http);
        Assert.Equal(TimeSpan.FromMinutes(2), http.Timeout);
        client.Dispose();

        using var response = await http.GetAsync("http://127.0.0.1/ping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            // Block indefinitely; StreamReader will call ReadAsync in async path, but provide sync fallback that also respects blocking
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

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_func(request));
    }
}
