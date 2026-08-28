using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Responder(request));
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
    public async Task SubscribeEvents_parses_durable_stream()
    {
        var sse =
            "id:5\nevent:message\n" +
            "data:{\"protocol_version\":1,\"event_id\":\"evt_1\",\"run_id\":\"run_12345678901234567890123456789012\"," +
            "\"session_id\":\"sess_1\",\"sequence\":5,\"event_type\":\"run.state_changed\"," +
            "\"occurred_at\":\"2026\",\"status\":\"running\",\"safe_summary\":\"started\"}\n\n";
        var handler = new FakeHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            },
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
