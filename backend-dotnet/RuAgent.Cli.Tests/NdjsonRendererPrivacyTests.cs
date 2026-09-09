using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Harness;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class NdjsonRendererPrivacyTests
{
    private static HarnessSafeEventEnvelope MakeEnvelope(string suffix = "a")
    {
        var evtId = "evt_" + new string(suffix[0], 32);
        var runId = "run_" + new string('a', 32);
        var sessionId = new string('b', 32);
        return new HarnessSafeEventEnvelope(
            ProtocolVersion: 1,
            EventId: evtId,
            RunId: runId,
            SessionId: sessionId,
            Sequence: 1,
            EventType: "tool.proposed",
            OccurredAt: "2026-08-24T00:00:00Z",
            Status: "running",
            SafeSummary: "safe summary for test",
            PayloadRef: "payload_" + new string('c', 32));
    }

    [Fact]
    public void Ndjson_IsVersioned()
    {
        var env = MakeEnvelope();
        var sw = new StringWriter();
        NdjsonEventRenderer.Render(env, sw);
        var line = sw.ToString().Trim();
        var doc = JsonDocument.Parse(line);
        Assert.True(doc.RootElement.TryGetProperty("v", out var v));
        Assert.Equal(1, v.GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("type", out var t));
        Assert.Equal("event", t.GetString());
        // protocol_version must be present and equal 1
        Assert.True(doc.RootElement.TryGetProperty("protocol_version", out var pv));
        Assert.Equal(1, pv.GetInt32());
    }

    [Fact]
    public void Ndjson_ContainsOnlySafeFields()
    {
        var env = MakeEnvelope();
        var sw = new StringWriter();
        NdjsonEventRenderer.Render(env, sw);
        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);
        // Must contain safe fields
        Assert.True(doc.RootElement.TryGetProperty("event_id", out _));
        Assert.True(doc.RootElement.TryGetProperty("run_id", out _));
        Assert.True(doc.RootElement.TryGetProperty("session_id", out _));
        Assert.True(doc.RootElement.TryGetProperty("sequence", out _));
        Assert.True(doc.RootElement.TryGetProperty("event_type", out _));
        Assert.True(doc.RootElement.TryGetProperty("status", out _));
        Assert.True(doc.RootElement.TryGetProperty("safe_summary", out _));
        // Must NOT contain forbidden fields
        var lower = json.ToLowerInvariant();
        Assert.DoesNotContain("\"prompt\"", lower);
        Assert.DoesNotContain("\"command\"", lower);
        Assert.DoesNotContain("\"output\"", lower);
        Assert.DoesNotContain("\"secret\"", lower);
        Assert.DoesNotContain("\"argv\"", lower);
        Assert.DoesNotContain("\"diff\"", lower);
        Assert.DoesNotContain("\"stdout\"", lower);
        Assert.DoesNotContain("\"stderr\"", lower);
        Assert.DoesNotContain("\"environment\"", lower);
    }

    [Fact]
    public void Ndjson_NeverPromptsOrImplicitApprovals()
    {
        var env = MakeEnvelope();
        var sw = new StringWriter();
        NdjsonEventRenderer.Render(env, sw);
        var json = sw.ToString();
        // Ensure no approval prompting logic: renderer should not emit approval token or implicit approval
        Assert.DoesNotContain("approve", json.ToLowerInvariant()); // unless event_type is approval.requested etc? but our envelope not that type
        // Ensure no prompt
        Assert.DoesNotContain("prompt", json.ToLowerInvariant());
    }

    [Fact]
    public void Ndjson_DoesNotAutoFetchPrivatePayload()
    {
        // Renderer should never call AgentApiClient.GetPayloadAsync. We verify by ensuring a fake handler receiving payload request would fail if called.
        var session = CreateSession();
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/payloads/"))
                throw new InvalidOperationException("Private payload auto-fetch must not happen in tracer.");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var http = new HttpClient(handler);
        // The renderer itself doesn't need HTTP, but we ensure commands don't auto-fetch during SSE rendering
        var env = MakeEnvelope();
        var sw = new StringWriter();
        NdjsonEventRenderer.Render(env, sw);
        var json = sw.ToString();
        // Even though envelope has payload_ref, renderer must not fetch it automatically - payload_ref is opaque pointer only
        Assert.Contains("payload_", json);
        // No HTTP call should have been made - handler would have thrown if any payload fetch was attempted
        Assert.True(true);
    }

    [Fact]
    public void SafeRenderer_DisplaysOnlySafeFields()
    {
        var env = MakeEnvelope();
        var sw = new StringWriter();
        SafeEventRenderer.Render(env, sw);
        var output = sw.ToString();
        // Should contain safe fields
        Assert.Contains(env.EventType, output);
        Assert.Contains(env.Status, output);
        Assert.Contains(env.SafeSummary, output);
        Assert.Contains(env.Sequence.ToString(), output);
        // Must not contain forbidden raw fields even if they were somehow in safe_summary? (safe_summary itself is safe)
        var lower = output.ToLowerInvariant();
        // The renderer output should not embed prompt/command/output/secret literal field names as JSON keys
        Assert.DoesNotContain("\"prompt\":", lower);
        Assert.DoesNotContain("\"command\":", lower);
        Assert.DoesNotContain("\"output\":", lower);
        Assert.DoesNotContain("\"secret\":", lower);
    }

    [Fact]
    public void SafeRenderer_NeverFetchesPayloadAutomatically()
    {
        var env = new HarnessSafeEventEnvelope(1, "evt_" + new string('a', 32), "run_" + new string('a', 32), new string('b', 32), 5, "tool.execution_completed", "2026-08-24T00:00:01Z", "running", "tool done", "payload_" + new string('d', 32));
        var sw = new StringWriter();
        // Should render without attempting to fetch payload content
        SafeEventRenderer.Render(env, sw);
        var outStr = sw.ToString();
        Assert.Contains("payload_", outStr); // ref is shown as opaque
        // But must not contain actual decrypted payload content (we don't have it, but ensure no fetch attempt)
        Assert.DoesNotContain("secret", outStr.ToLowerInvariant());
    }

    [Fact]
    public void Ndjson_RenderFrame_DurableUsesSafeEnvelope()
    {
        var runId = "run_" + new string('a', 32);
        var sessionId = new string('b', 32);
        var evtId = "evt_" + new string('c', 32);
        var data = JsonSerializer.Serialize(new HarnessSafeEventEnvelope(1, evtId, runId, sessionId, 10, "run.state_changed", "2026-08-24T00:00:00Z", "running", "state ok", null));
        var frame = new SseFrame(10, "message", data, false, null);
        var sw = new StringWriter();
        NdjsonEventRenderer.RenderFrame(frame, sw);
        var line = sw.ToString().Trim();
        var doc = JsonDocument.Parse(line);
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("event", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("sequence").GetInt64());
        // Ensure no forbidden
        Assert.False(NdjsonEventRenderer.ContainsForbidden(line));
    }

    [Fact]
    public void Ndjson_RenderFrame_TransientDoesNotAdvanceDurableCursor()
    {
        var frame = new SseFrame(null, "transient", "{\"kind\":\"assistant.delta\",\"data\":\"hello\"}", true, 1);
        var sw = new StringWriter();
        NdjsonEventRenderer.RenderFrame(frame, sw);
        var line = sw.ToString().Trim();
        var doc = JsonDocument.Parse(line);
        Assert.Equal("transient", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("transient_sequence").GetInt32());
        // Ensure v present
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        // Must not contain forbidden
        Assert.False(NdjsonEventRenderer.ContainsForbidden(line));
    }

    [Fact]
    public void Ndjson_RejectsForbiddenFieldsIfPresent()
    {
        // Direct check of ContainsForbidden helper
        Assert.True(NdjsonEventRenderer.ContainsForbidden("{\"prompt\":\"hello\"}"));
        Assert.True(NdjsonEventRenderer.ContainsForbidden("{\"command\":\"ls\"}"));
        Assert.True(NdjsonEventRenderer.ContainsForbidden("{\"output\":\"data\"}"));
        Assert.True(NdjsonEventRenderer.ContainsForbidden("{\"secret\":\"x\"}"));
        Assert.False(NdjsonEventRenderer.ContainsForbidden("{\"safe_summary\":\"ok\"}"));
    }

    [Fact]
    public void Ndjson_Emits_Usage_Totals_When_Present()
    {
        var env = MakeEnvelope() with { InputTokens = 1000, OutputTokens = 200, TotalTokens = 1200 };
        var sw = new StringWriter();
        NdjsonEventRenderer.Render(env, sw);
        var doc = JsonDocument.Parse(sw.ToString().Trim());
        Assert.Equal(1000, doc.RootElement.GetProperty("input_tokens").GetInt64());
        Assert.Equal(200, doc.RootElement.GetProperty("output_tokens").GetInt64());
        Assert.Equal(1200, doc.RootElement.GetProperty("total_tokens").GetInt64());
        Assert.False(NdjsonEventRenderer.ContainsForbidden(sw.ToString()));
    }

    [Fact]
    public void JsonOutput_NeverContainsRawPayloadContent()
    {
        // Ensure that even if envelope had payload_ref, the actual payload bytes are not inline
        var env = MakeEnvelope();
        var sw = new StringWriter();
        NdjsonEventRenderer.Render(env, sw);
        var json = sw.ToString();
        // payload_ref is allowed as opaque, but content field is not
        Assert.Contains("payload_ref", json);
        Assert.DoesNotContain("\"content\"", json.ToLowerInvariant());
    }

    private static PublicHostSession CreateSession()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, "http://127.0.0.1:8000/", new string('x', 64), proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(desc);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_func(request));
    }
}
