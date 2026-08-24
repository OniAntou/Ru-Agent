using System.Text.Json;
using RuBackend.Diagnostics;
using RuBackend.Harness;
using RuAgent.Harness.Core;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HarnessDiagnosticEventFactoryTests
{
    [Fact]
    public void Factory_creates_event_with_bounded_ids_and_no_raw_fields()
    {
        var runId = new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var stepId = new ModelStepId("step_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var callId = new ToolCallId("call_cccccccccccccccccccccccccccccccc");
        var execId = new ExecutionId("exec_dddddddddddddddddddddddddddddddd");
        var evt = HarnessDiagnosticEventFactory.Create(
            DiagnosticCodes.HarnessRunCreated,
            runId: runId,
            modelStepId: stepId,
            toolCallId: callId,
            executionId: execId,
            durationMs: 123.45,
            byteCount: 1024,
            truncated: true,
            status: "started",
            errorCode: "harness_test_code");

        Assert.Equal(runId.Value, evt.RunId);
        Assert.Equal(stepId.Value, evt.ModelStepId);
        Assert.Equal(callId.Value, evt.ToolCallId);
        Assert.Equal(execId.Value, evt.ExecutionId);
        Assert.Equal(123.45, evt.DurationMs);
        Assert.Equal(1024, evt.ByteCount);
        Assert.True(evt.Truncated);

        var json = DiagnosticEventPolicy.Serialize(evt);
        Assert.Contains("\"run_id\":\"run_aaaa", json);
        Assert.Contains("\"model_step_id\":\"step_bbbb", json);
        // Must not contain forbidden raw fields
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("argv", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stdout", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("prompt: hello world with secret Bearer sk-123")]
    [InlineData("command: rm -rf /")]
    [InlineData("argv: [\"dotnet\", \"build\"]")]
    [InlineData("path: C:\\secret\\file.txt")]
    [InlineData("code: console.log('x')")]
    [InlineData("diff: @@ -1 +1 @@")]
    [InlineData("output: stdout stderr")]
    [InlineData("provider body: {\"key\":\"sk-xyz\"}")]
    [InlineData("environment: TOKEN=secret")]
    [InlineData("exception message: NullReferenceException at Foo")]
    public void Factory_boundary_filters_raw_leakage_strings(string raw)
    {
        // Feed raw strings as if they were trying to leak via errorCode or other bounded field
        // Factory sanitizes errorCode to safe pattern, so raw with spaces/special chars should be dropped
        var evt = HarnessDiagnosticEventFactory.Create(
            DiagnosticCodes.HarnessToolProposed,
            runId: new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            errorCode: raw);

        var json = DiagnosticEventPolicy.Serialize(evt);
        // Must not contain any substring of raw that is not safe
        Assert.DoesNotContain("Bearer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stdout", json, StringComparison.OrdinalIgnoreCase);
        // error_code should be sanitized to null or safe token, not raw
        if (json.Contains("\"error_code\""))
        {
            using var doc = JsonDocument.Parse(json);
            var ec = doc.RootElement.GetProperty("error_code").GetString()!;
            Assert.Matches("^[a-z0-9_.-]{1,64}$", ec);
            Assert.DoesNotContain(" ", ec);
        }
    }

    [Fact]
    public void Factory_rejects_unknown_protocol_fields_via_policy()
    {
        var rawJson = """
        {
            "event_id": "evt_abc",
            "ts": 1720000000.1,
            "iso": "2026-08-20T00:00:00.100Z",
            "level": "info",
            "component": "host.harness",
            "event_code": "harness.run.created",
            "run_id": "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "prompt": "should be filtered",
            "command": "rm -rf",
            "output": "stdout",
            "extra_field": 123
        }
        """;

        var parsed = DiagnosticEventPolicy.Deserialize(rawJson);
        Assert.NotNull(parsed);
        var serialized = DiagnosticEventPolicy.Serialize(parsed!);
        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("output", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("extra_field", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"run_id\"", serialized);
    }

    [Fact]
    public void Factory_ensures_bounded_byte_count_and_truncated()
    {
        var evt = HarnessDiagnosticEventFactory.Create(
            DiagnosticCodes.HarnessToolExecutionCompleted,
            byteCount: 99999999, // exceeds max 16Mi
            truncated: true);

        // Should be sanitized to null due to out of bound
        Assert.Null(evt.ByteCount);

        var evt2 = HarnessDiagnosticEventFactory.Create(
            DiagnosticCodes.HarnessToolExecutionCompleted,
            byteCount: 1024,
            truncated: false);
        Assert.Equal(1024, evt2.ByteCount);
        Assert.False(evt2.Truncated);
    }

    [Fact]
    public void Factory_uses_stable_harness_codes_only()
    {
        var evt = HarnessDiagnosticEventFactory.Create("unknown_code_xyz");
        // Should be sanitized to diagnostic_unclassified
        Assert.Equal(DiagnosticCodes.Unclassified, evt.EventCode);

        var evt2 = HarnessDiagnosticEventFactory.Create(DiagnosticCodes.HarnessRunCreated);
        Assert.Equal(DiagnosticCodes.HarnessRunCreated, evt2.EventCode);
    }

    [Fact]
    public void Diagnostic_jsonl_contains_only_stable_codes_and_bounded_ids()
    {
        var runId = new RunId("run_11111111111111111111111111111111");
        var evt = HarnessDiagnosticEventFactory.Create(
            DiagnosticCodes.HarnessModelStepStarted,
            runId: runId,
            durationMs: 42.0);

        var json = DiagnosticEventPolicy.Serialize(evt);
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Contains("event_code", props);
        Assert.Contains("run_id", props);
        Assert.Contains("duration_ms", props);
        // Must not contain raw fields
        Assert.DoesNotContain("prompt", props);
        Assert.DoesNotContain("stdout", props);
        Assert.Equal("harness.model.step_started", doc.RootElement.GetProperty("event_code").GetString());
    }
}
