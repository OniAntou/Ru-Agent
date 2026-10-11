using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Cli.Rpc;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests.Rpc;

public sealed class RpcRunBridgeTests
{
    private const string RunId = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string Envelope(long sequence, string eventType, string status, string summary = "step")
    {
        var safeSummary = summary.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return "{\"protocol_version\":1,\"event_id\":\"evt_" + sequence + "\",\"run_id\":\"" + RunId +
            "\",\"session_id\":\"s1\",\"sequence\":" + sequence + ",\"event_type\":\"" + eventType +
            "\",\"occurred_at\":\"2026-09-29T00:00:00Z\",\"status\":\"" + status +
            "\",\"safe_summary\":\"" + safeSummary + "\",\"payload_ref\":null}";
    }

    private static HttpClient Sse(string body) =>
        RpcTestHost.CreateClient(_ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        }));

    private static HttpClient TerminalRun() =>
        RpcTestHost.CreateClient(_ => Task.FromResult(RpcTestHost.Json(
            "{\"run_id\":\"" + RunId + "\",\"session_id\":\"s1\",\"workspace_id\":\"w1\",\"status\":\"completed\"," +
            "\"history_wait_kind\":null,\"preset\":\"standard\",\"policy_snapshot_version\":1," +
            "\"policy_snapshot_sha256\":\"x\",\"version\":2,\"last_sequence\":3," +
            "\"created_at\":\"2026-09-29T00:00:00Z\",\"updated_at\":\"2026-09-29T00:00:00Z\"," +
            "\"terminal_at\":\"2026-09-29T00:00:01Z\",\"terminal_error_code\":null}")));

    private static HttpClient RunningRun() =>
        RpcTestHost.CreateClient(_ => Task.FromResult(RpcTestHost.Json(
            "{\"run_id\":\"" + RunId + "\",\"session_id\":\"s1\",\"workspace_id\":\"w1\",\"status\":\"running\"," +
            "\"history_wait_kind\":null,\"preset\":\"standard\",\"policy_snapshot_version\":1," +
            "\"policy_snapshot_sha256\":\"x\",\"version\":2,\"last_sequence\":1," +
            "\"created_at\":\"2026-09-29T00:00:00Z\",\"updated_at\":\"2026-09-29T00:00:00Z\"," +
            "\"terminal_at\":null,\"terminal_error_code\":null}")));

    [Fact]
    public async Task Attach_publishes_durable_events_with_the_subscription_id()
    {
        var published = new List<JsonObject>();
        var body = "id: 1\nevent: durable\ndata: " + Envelope(1, "run.started", "running") + "\n\n";
        using var bridge = new RpcRunBridge(
            new AgentSseClient(RpcTestHost.CreateSession(), Sse(body)),
            new AgentApiClient(RpcTestHost.CreateSession(), TerminalRun()),
            publish: notification =>
            {
                published.Add(notification);
                return true;
            });

        var subscriptionId = bridge.Attach(RunId, 0, includeTransient: false);
        await bridge.WaitForSubscriptionsAsync(TimeSpan.FromSeconds(5));

        var runEvent = Assert.Single(published, p => p["method"]!.GetValue<string>() == "run.event");
        var parameters = runEvent["params"]!.AsObject();
        Assert.Equal(subscriptionId, parameters["subscription_id"]!.GetValue<string>());
        Assert.Equal(1, parameters["sequence"]!.GetValue<long>());
        Assert.Equal("run.started", parameters["event_type"]!.GetValue<string>());
        Assert.DoesNotContain("prompt", runEvent.ToJsonString());
        Assert.DoesNotContain(published, p => p["method"]!.GetValue<string>() == "run.detached");
    }

    [Fact]
    public async Task A_stream_that_dies_before_terminal_publishes_run_detached()
    {
        var published = new List<JsonObject>();
        using var bridge = new RpcRunBridge(
            new AgentSseClient(
                RpcTestHost.CreateSession(),
                RpcTestHost.CreateClient(_ => Task.FromResult(RpcTestHost.Json(
                    "{\"error_code\":\"sse_failed\",\"message\":\"stream is gone\"}",
                    System.Net.HttpStatusCode.InternalServerError)))),
            new AgentApiClient(RpcTestHost.CreateSession(), RunningRun()),
            publish: notification =>
            {
                published.Add(notification);
                return true;
            });

        var subscriptionId = bridge.Attach(RunId, 0, includeTransient: false);
        await bridge.WaitForSubscriptionsAsync(TimeSpan.FromSeconds(5));

        var detached = Assert.Single(published, p => p["method"]!.GetValue<string>() == "run.detached");
        var parameters = detached["params"]!.AsObject();
        Assert.Equal(subscriptionId, parameters["subscription_id"]!.GetValue<string>());
        Assert.Equal("stream_interrupted", parameters["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_summary_that_quotes_a_word_is_delivered_not_filtered()
    {
        // Structural privacy filter: prose that merely quotes a word like
        // "output" is not a raw-content field, and dropping such an event could
        // suppress the terminal frame a consumer waits on.
        var published = new List<JsonObject>();
        var body = "id: 1\nevent: durable\ndata: "
            + Envelope(1, "run.started", "running", "read \"output\" ok") + "\n\n";
        using var bridge = new RpcRunBridge(
            new AgentSseClient(RpcTestHost.CreateSession(), Sse(body)),
            new AgentApiClient(RpcTestHost.CreateSession(), TerminalRun()),
            publish: notification =>
            {
                published.Add(notification);
                return true;
            });

        bridge.Attach(RunId, 0, includeTransient: false);
        await bridge.WaitForSubscriptionsAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(published, p => p["method"]!.GetValue<string>() == "run.event");
        Assert.DoesNotContain(published, p => p["method"]!.GetValue<string>() == "run.detached");
    }

    [Fact]
    public async Task An_embedded_payload_in_a_summary_ends_the_subscription_loudly()
    {
        var published = new List<JsonObject>();
        var body = "id: 1\nevent: durable\ndata: "
            + Envelope(1, "run.started", "running", "{\"stdout\":\"raw\"}") + "\n\n";
        using var bridge = new RpcRunBridge(
            new AgentSseClient(RpcTestHost.CreateSession(), Sse(body)),
            new AgentApiClient(RpcTestHost.CreateSession(), TerminalRun()),
            publish: notification =>
            {
                published.Add(notification);
                return true;
            });

        bridge.Attach(RunId, 0, includeTransient: false);
        await bridge.WaitForSubscriptionsAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(published, p => p["method"]!.GetValue<string>() == "run.event");
        var detached = Assert.Single(published, p => p["method"]!.GetValue<string>() == "run.detached");
        Assert.Equal("event_filtered", detached["params"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Transient_frames_are_withheld_unless_requested()
    {
        var published = new List<JsonObject>();
        var body =
            "event: transient\nid: t1\ndata: {\"delta\":\"hello\"}\n\n" +
            "id: 1\nevent: durable\ndata: " + Envelope(1, "run.started", "running") + "\n\n";
        using var bridge = new RpcRunBridge(
            new AgentSseClient(RpcTestHost.CreateSession(), Sse(body)),
            new AgentApiClient(RpcTestHost.CreateSession(), TerminalRun()),
            publish: notification =>
            {
                published.Add(notification);
                return true;
            });

        bridge.Attach(RunId, 0, includeTransient: false);
        await bridge.WaitForSubscriptionsAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(published, p => p["method"]!.GetValue<string>() == "run.transient");
        Assert.Contains(published, p => p["method"]!.GetValue<string>() == "run.event");
    }

    [Fact]
    public async Task Transient_frames_are_delivered_when_requested()
    {
        var published = new List<JsonObject>();
        var body =
            "event: transient\nid: t1\ndata: {\"delta\":\"hello\"}\n\n" +
            "id: 1\nevent: durable\ndata: " + Envelope(1, "run.started", "running") + "\n\n";
        using var bridge = new RpcRunBridge(
            new AgentSseClient(RpcTestHost.CreateSession(), Sse(body)),
            new AgentApiClient(RpcTestHost.CreateSession(), TerminalRun()),
            publish: notification =>
            {
                published.Add(notification);
                return true;
            });

        bridge.Attach(RunId, 0, includeTransient: true);
        await bridge.WaitForSubscriptionsAsync(TimeSpan.FromSeconds(5));
        var transient = Assert.Single(published, p => p["method"]!.GetValue<string>() == "run.transient");
        Assert.Equal("{\"delta\":\"hello\"}", transient["params"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_full_publish_queue_stops_the_pump_without_dropping_silently()
    {
        var published = new List<JsonObject>();
        var body =
            "id: 1\nevent: durable\ndata: " + Envelope(1, "run.started", "running") + "\n\n" +
            "id: 2\nevent: durable\ndata: " + Envelope(2, "model.step_started", "running") + "\n\n" +
            "id: 3\nevent: durable\ndata: " + Envelope(3, "run.state_changed", "running") + "\n\n";
        using var bridge = new RpcRunBridge(
            new AgentSseClient(RpcTestHost.CreateSession(), Sse(body)),
            new AgentApiClient(RpcTestHost.CreateSession(), TerminalRun()),
            publish: notification =>
            {
                published.Add(notification);
                return published.Count != 1; // full queue on the first event only
            });

        bridge.Attach(RunId, 0, includeTransient: false);
        await bridge.WaitForSubscriptionsAsync(TimeSpan.FromSeconds(5));
        Assert.Single(published);
    }

    [Fact]
    public void Detach_stops_an_active_subscription()
    {
        var body = "id: 1\nevent: durable\ndata: " + Envelope(1, "run.started", "running") + "\n\n";
        using var bridge = new RpcRunBridge(
            new AgentSseClient(RpcTestHost.CreateSession(), Sse(body)),
            new AgentApiClient(RpcTestHost.CreateSession(), TerminalRun()),
            publish: _ => true);
        var subscriptionId = bridge.Attach(RunId, 0, includeTransient: false);
        Assert.True(bridge.Detach(subscriptionId));
        Assert.False(bridge.Detach(subscriptionId));
    }
}
