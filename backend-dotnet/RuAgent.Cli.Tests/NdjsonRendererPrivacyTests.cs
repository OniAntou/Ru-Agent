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
        // The renderer keeps payload_ref an opaque pointer: it emits the ref and
        // nothing that could resolve it.
        var env = MakeEnvelope();
        var sw = new StringWriter();
        NdjsonEventRenderer.Render(env, sw);
        var json = sw.ToString();
        Assert.Contains("payload_", json);

        // And it cannot resolve one: no entry point takes an HTTP client or an
        // API client, so no code path inside the renderer can fetch a payload.
        var renderMethods = typeof(NdjsonEventRenderer)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(method => method.Name.StartsWith("Render", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(renderMethods);
        Assert.All(renderMethods, method =>
            Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(HttpClient)
                    || parameter.ParameterType == typeof(AgentApiClient)));
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

    /// <summary>
    /// A durable frame the privacy guard rejects is skipped with an error line
    /// instead of throwing: the cursor has already advanced past it, and an
    /// exception would kill the rest of the stream (terminal snapshot included).
    /// </summary>
    [Fact]
    public void Ndjson_RenderFrame_SkipsAForbiddenFrameInsteadOfThrowing()
    {
        var data = JsonSerializer.Serialize(new HarnessSafeEventEnvelope(
            1,
            "evt_" + new string('a', 32),
            "run_" + new string('a', 32),
            new string('b', 32),
            3,
            "run.state_changed",
            "2026",
            "running",
            "the \"output\" field",
            null));
        var frame = new SseFrame(3, "message", data, false, null);

        var sw = new StringWriter();
        NdjsonEventRenderer.RenderFrame(frame, sw);

        var doc = JsonDocument.Parse(sw.ToString().Trim());
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
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

    /// <summary>
    /// The guard is applied to a serialized line, and System.Text.Json escapes a
    /// content quote as <c>\u0022</c>. Before this was handled the check could never
    /// fire on real serializer output — every forbidden name arrived escaped — so
    /// the raw stream text it was meant to catch passed through.
    /// </summary>
    [Fact]
    public void ContainsForbidden_SeesForbiddenNamesThroughJsonEscaping()
    {
        var escaped = JsonSerializer.Serialize(new
        {
            v = 1,
            type = "transient",
            data_preview = "{\"command\":\"git push origin main\",\"output\":\"stdout bytes\"}",
        });

        // The literal spelling is absent from the payload, which is why the old
        // substring check was blind to it.
        Assert.DoesNotContain("\"command\"", escaped, StringComparison.Ordinal);
        Assert.True(NdjsonEventRenderer.ContainsForbidden(escaped));
    }

    /// <summary>
    /// A transient frame carries raw model text or tool output. The documented
    /// tracer contract is safe envelope fields only, so the payload text is
    /// withheld and only its size is reported.
    /// </summary>
    [Fact]
    public void Ndjson_TransientFrameNeverInlinesRawStreamContent()
    {
        const string rawContent = "SECRET_TOOL_STDOUT_0123456789";
        var frame = new SseFrame(null, "transient", rawContent, true, 4);

        var sw = new StringWriter();
        NdjsonEventRenderer.RenderFrame(frame, sw);
        var line = sw.ToString().Trim();

        Assert.DoesNotContain(rawContent, line, StringComparison.Ordinal);
        Assert.DoesNotContain("data_preview", line, StringComparison.Ordinal);
        var doc = JsonDocument.Parse(line);
        Assert.Equal(rawContent.Length, doc.RootElement.GetProperty("data_bytes").GetInt32());
    }

    /// <summary>
    /// The field is named data_bytes, so a non-ASCII payload must report its
    /// UTF-8 wire size, not its UTF-16 char count.
    /// </summary>
    [Fact]
    public void Ndjson_TransientFrameReportsUtf8ByteCount()
    {
        const string multibyte = "mèo 🐱";
        var frame = new SseFrame(null, "transient", multibyte, true, 5);

        var sw = new StringWriter();
        NdjsonEventRenderer.RenderFrame(frame, sw);
        var doc = JsonDocument.Parse(sw.ToString().Trim());

        int reported = doc.RootElement.GetProperty("data_bytes").GetInt32();
        Assert.Equal(Encoding.UTF8.GetByteCount(multibyte), reported);
        Assert.NotEqual(multibyte.Length, reported);
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
}
