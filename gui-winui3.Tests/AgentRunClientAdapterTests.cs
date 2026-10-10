using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentRunClientAdapterTests
{
    private const string RunId = "run_12345678901234567890123456789012";
    private const string Bearer = "test-token";

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncResponder { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return AsyncResponder is null
                ? Task.FromResult(Responder(request))
                : AsyncResponder(request, cancellationToken);
        }
    }

    private static PublicHostSession MakeSession() =>
        PublicHostSession.FromDescriptor(new RuntimeDescriptor(
            RuntimeDescriptor.CurrentProtocolVersion,
            "http://127.0.0.1:5180/",
            Bearer,
            Environment.ProcessId,
            DateTime.UtcNow.ToString("O"),
            Guid.NewGuid().ToString("N"),
            DateTime.UtcNow.ToString("O")));

    [Fact]
    public async Task CreateRun_calls_shared_agent_api()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"run_id\":\"run_12345678901234567890123456789012\",\"session_id\":\"sess_1\"," +
                    "\"workspace_id\":\"ws_1\",\"status\":\"running\",\"preset\":\"standard\"," +
                    "\"created_at\":\"2026\",\"updated_at\":\"2026\"}",
                    Encoding.UTF8,
                    "application/json"),
            },
        };
        using var http = new HttpClient(handler);
        var adapter = new AgentRunClientAdapter(MakeSession(), http);

        var run = await adapter.CreateRunAsync(
            new CreateAgentRunRequest("sess_1", "C:\\work", "do it", "standard", null, "idem-1"));

        Assert.Equal(RunId, run.RunId);
        Assert.Contains(
            handler.Requests,
            r => r.RequestUri!.ToString().Contains("agent/v1/runs") && r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task CreateRun_observes_the_response_after_the_caller_cancels_mid_flight()
    {
        // A create commits a run before it answers. A cancelled wait must not
        // abort the response, or the controller never learns the run id and the
        // committed run stays orphaned on the host.
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler
        {
            AsyncResponder = async (_, _) =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"run_id\":\"run_12345678901234567890123456789012\",\"session_id\":\"sess_1\"," +
                        "\"workspace_id\":\"ws_1\",\"status\":\"running\",\"preset\":\"standard\"," +
                        "\"created_at\":\"2026\",\"updated_at\":\"2026\"}",
                        Encoding.UTF8,
                        "application/json"),
                };
            },
        };
        using var http = new HttpClient(handler);
        var adapter = new AgentRunClientAdapter(MakeSession(), http);
        using var cts = new CancellationTokenSource();

        var createTask = adapter.CreateRunAsync(
            new CreateAgentRunRequest("sess_1", "C:\\work", "do it", "standard", null, "idem-late-create"),
            cts.Token);

        await entered.Task;
        cts.Cancel();
        release.TrySetResult(true);

        var run = await createTask;
        Assert.Equal(RunId, run.RunId);
    }

    [Fact]
    public async Task SubscribeEvents_parses_durable_stream()
    {
        var sse = DurableSseFrame(5, "started");
        var handler = new FakeHttpHandler
        {
            Responder = request => request.RequestUri!.ToString().Contains("/events", StringComparison.Ordinal)
                ? SseResponse(sse)
                : RunStatusResponse("completed"),
        };
        using var http = new HttpClient(handler);
        var adapter = new AgentRunClientAdapter(MakeSession(), http);

        var received = new List<AgentEventModel>();
        await adapter.SubscribeEventsAsync(RunId, 0, m =>
        {
            received.Add(m);
            return Task.CompletedTask;
        });

        Assert.Single(received);
        Assert.Equal("run.state_changed", received[0].EventType);
        Assert.Equal(5, received[0].Sequence);
    }

    [Fact]
    public async Task SubscribeEvents_resumes_from_the_last_durable_sequence_when_the_run_is_still_active()
    {
        var handler = new FakeHttpHandler();
        var eventsCalls = 0;
        handler.Responder = request =>
        {
            if (request.RequestUri!.ToString().Contains("/events", StringComparison.Ordinal))
            {
                eventsCalls++;
                // The first stream breaks after sequence 5; the second delivers 6.
                return SseResponse(DurableSseFrame(eventsCalls == 1 ? 5 : 6, eventsCalls == 1 ? "first" : "second"));
            }

            // The run is provably still active after the first break, terminal
            // only after the resumed stream ends.
            return RunStatusResponse(eventsCalls <= 1 ? "running" : "completed");
        };
        using var http = new HttpClient(handler);
        var adapter = new AgentRunClientAdapter(MakeSession(), http);

        var received = new List<AgentEventModel>();
        await adapter.SubscribeEventsAsync(RunId, 0, m =>
        {
            received.Add(m);
            return Task.CompletedTask;
        });

        Assert.Equal(2, received.Count);
        Assert.Equal(new long?[] { 5, 6 }, received.Select(item => item.Sequence).ToArray());
        Assert.Contains(
            handler.Requests,
            r => r.RequestUri!.Query.Contains("after_sequence=5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubscribeEvents_gives_up_after_bounded_resumes()
    {
        var handler = new FakeHttpHandler
        {
            Responder = request => request.RequestUri!.ToString().Contains("/events", StringComparison.Ordinal)
                ? SseResponse(DurableSseFrame(5, "started"))
                : RunStatusResponse("running"),
        };
        using var http = new HttpClient(handler);
        var adapter = new AgentRunClientAdapter(MakeSession(), http);

        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            adapter.SubscribeEventsAsync(RunId, 0, _ => Task.CompletedTask));

        Assert.Equal("stream_interrupted", error.ErrorCode);
    }

    private static HttpResponseMessage SseResponse(string sse) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };

    [Fact]
    public async Task SubscribeEvents_does_not_retry_a_callback_io_failure_as_a_transport_error()
    {
        var handler = new FakeHttpHandler
        {
            Responder = request => request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal)
                ? SseResponse(DurableSseFrame(5, "started"))
                : RunStatusResponse("completed"),
        };
        using var http = new HttpClient(handler);
        using var adapter = new AgentRunClientAdapter(MakeSession(), http);
        var failure = new IOException("renderer failed");
        var delivered = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => adapter.SubscribeEventsAsync(RunId, 0, _ =>
        {
            delivered++;
            throw failure;
        }));
        Assert.Same(failure, error);
        Assert.Equal(1, delivered);
        Assert.Single(handler.Requests);
    }

    private static HttpResponseMessage RunStatusResponse(string status) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"run_id\":\"run_12345678901234567890123456789012\",\"session_id\":\"sess_1\"," +
                $"\"workspace_id\":\"ws_1\",\"status\":\"{status}\",\"preset\":\"standard\",\"policy_snapshot_version\":1," +
                "\"policy_snapshot_sha256\":\"\",\"version\":1,\"last_sequence\":6," +
                "\"created_at\":\"2026\",\"updated_at\":\"2026\"}",
                Encoding.UTF8,
                "application/json"),
        };

    private static string DurableSseFrame(long sequence, string summary) =>
        $"id:{sequence}\nevent:message\n" +
        "data:{\"protocol_version\":1,\"event_id\":\"evt_1\",\"run_id\":\"run_12345678901234567890123456789012\"," +
        "\"session_id\":\"sess_1\",\"sequence\":" + sequence + ",\"event_type\":\"run.state_changed\"," +
        "\"occurred_at\":\"2026\",\"status\":\"running\",\"safe_summary\":\"" + summary + "\"}\n\n";

    [Fact]
    public async Task SubscribeEvents_delivers_only_valid_agent_activity_to_separate_callback()
    {
        var inner = "{\"activity_id\":\"act_11111111111111111111111111111111\",\"kind\":\"analyze\",\"state\":\"completed\",\"file_name\":\"ChatView.xaml.cs\",\"safe_code\":\"succeeded\"}";
        var outer = JsonSerializer.Serialize(new { kind = "agent.activity", data = inner });
        var raw = JsonSerializer.Serialize(new { kind = "tool.output.delta", data = "secret" });
        var sse =
            $"event: transient\nid: t7\ndata: {outer}\n\n" +
            $"event: transient\nid: t8\ndata: {raw}\n\n";
        var handler = new FakeHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            },
        };
        using var http = new HttpClient(handler);
        var adapter = new AgentRunClientAdapter(MakeSession(), http);
        var received = new List<AgentActivityModel>();

        await adapter.SubscribeEventsAsync(
            RunId,
            0,
            _ => Task.CompletedTask,
            onActivity: activity =>
            {
                received.Add(activity);
                return Task.CompletedTask;
            });

        var activity = Assert.Single(received);
        Assert.Equal(AgentActivityKind.Analyze, activity.Kind);
        Assert.Equal(7, activity.TransientSequence);
        Assert.Null(activity.DurableSequence);
    }

    [Fact]
    public async Task SubscribeEvents_delivers_typed_summary_to_summary_callback()
    {
        var inner = "{\"status\":\"succeeded\",\"verification_state\":\"verified\",\"workspace_label\":\"Test Agent\",\"files_scanned\":5,\"files_changed\":1}";
        var outer = JsonSerializer.Serialize(new { kind = "agent.summary", data = inner });
        var sse = $"event: transient\nid: t9\ndata: {outer}\n\n";
        var handler = new FakeHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            },
        };
        using var http = new HttpClient(handler);
        var adapter = new AgentRunClientAdapter(MakeSession(), http);
        var received = new List<AgentRunSummaryModel>();

        await adapter.SubscribeEventsAsync(
            RunId,
            0,
            _ => Task.CompletedTask,
            onSummary: summary =>
            {
                received.Add(summary);
                return Task.CompletedTask;
            });

        var summary = Assert.Single(received);
        Assert.Equal("succeeded", summary.Status);
        Assert.Equal("verified", summary.VerificationState);
        Assert.Equal(9, summary.TransientSequence);
    }

    [Fact]
    public async Task SubscribeEvents_delivers_typed_assistant_delta_and_drops_raw_tool_data()
    {
        var inner = "{\"text\":\"Xin chào anh\",\"model_step_id\":\"step_0123456789abcdef0123456789abcdef\",\"execution_id\":null}";
        var assistant = JsonSerializer.Serialize(new { kind = "assistant.delta", data = inner });
        var rawTool = JsonSerializer.Serialize(new { kind = "tool.output.delta", data = "secret command" });
        var malformed = JsonSerializer.Serialize(new { kind = "assistant.delta", data = "{\"text\":\"leak\"}" });
        var sse =
            $"event: transient\nid: t10\ndata: {assistant}\n\n" +
            $"event: transient\nid: t11\ndata: {rawTool}\n\n" +
            $"event: transient\nid: t12\ndata: {malformed}\n\n";
        var handler = new FakeHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            },
        };
        using var http = new HttpClient(handler);
        var adapter = new AgentRunClientAdapter(MakeSession(), http);
        var received = new List<AgentAssistantDeltaModel>();

        await adapter.SubscribeEventsAsync(
            RunId,
            0,
            _ => Task.CompletedTask,
            onAssistantDelta: delta =>
            {
                received.Add(delta);
                return Task.CompletedTask;
            });

        var delta = Assert.Single(received);
        Assert.Equal("Xin chào anh", delta.Text);
        Assert.Equal(10, delta.TransientSequence);
    }

    [Fact]
    public async Task SubscribeEvents_unknown_version_fails_closed()
    {
        var sse =
            "id:5\nevent:message\n" +
            "data:{\"protocol_version\":999,\"event_id\":\"e\",\"run_id\":\"run_12345678901234567890123456789012\"," +
            "\"session_id\":\"s\",\"sequence\":5,\"event_type\":\"x\"," +
            "\"occurred_at\":\"2026\",\"status\":\"running\",\"safe_summary\":\"y\"}\n\n";
        var handler = new FakeHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            },
        };
        using var http = new HttpClient(handler);
        var adapter = new AgentRunClientAdapter(MakeSession(), http);

        await Assert.ThrowsAsync<AgentApiException>(() =>
            adapter.SubscribeEventsAsync(RunId, 0, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task GetPayload_run_mismatch_throws()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"run_id\":\"run_DIFFERENT000000000000000000000012\",\"content\":\"secret\"}",
                    Encoding.UTF8,
                    "application/json"),
            },
        };
        using var http = new HttpClient(handler);
        var adapter = new AgentRunClientAdapter(MakeSession(), http);

        await Assert.ThrowsAsync<AgentApiException>(() =>
            adapter.GetPayloadAsync(RunId, "payload_1234567890123456789012345678901234"));
    }
}
