using System.Text.Json;
using RuBackend.Diagnostics;
using Xunit;

namespace RuBackend.Tests.Diagnostics;

public sealed class DiagnosticEventTests
{
    [Fact]
    public void Serializer_emits_only_documented_fields_and_omits_nulls()
    {
        var evt = new DiagnosticEvent(
            EventId: "evt_1234567890abcdef",
            Ts: 1720000000.123,
            Iso: "2026-08-20T00:00:00.123Z",
            Level: "warning",
            Component: "worker.voice",
            EventCode: "voice_unavailable",
            CorrelationId: "c_abcdef1234567890",
            DurationMs: 42.5,
            Status: "failed",
            ErrorCode: "worker_unavailable",
            ExceptionKind: "timeout");

        var json = DiagnosticEventPolicy.Serialize(evt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var propertyNames = root.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        var expectedNames = new[]
        {
            "component",
            "correlation_id",
            "duration_ms",
            "error_code",
            "event_code",
            "event_id",
            "exception_kind",
            "iso",
            "level",
            "status",
            "ts",
        }.OrderBy(n => n).ToArray();

        Assert.Equal(expectedNames, propertyNames);
        Assert.Equal("evt_1234567890abcdef", root.GetProperty("event_id").GetString());
        Assert.Equal(1720000000.123, root.GetProperty("ts").GetDouble());
        Assert.Equal("warning", root.GetProperty("level").GetString());
        Assert.Equal("worker.voice", root.GetProperty("component").GetString());
        Assert.Equal("voice_unavailable", root.GetProperty("event_code").GetString());
        Assert.Equal("c_abcdef1234567890", root.GetProperty("correlation_id").GetString());
        Assert.Equal(42.5, root.GetProperty("duration_ms").GetDouble());
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal("worker_unavailable", root.GetProperty("error_code").GetString());
        Assert.Equal("timeout", root.GetProperty("exception_kind").GetString());
    }

    [Fact]
    public void Serializer_omits_null_optional_fields()
    {
        var evt = new DiagnosticEvent(
            EventId: "evt_min",
            Ts: 1720000000.0,
            Iso: "2026-08-20T00:00:00.000Z",
            Level: "info",
            Component: "host",
            EventCode: "host_startup");

        var json = DiagnosticEventPolicy.Serialize(evt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("correlation_id", out _));
        Assert.False(root.TryGetProperty("duration_ms", out _));
        Assert.False(root.TryGetProperty("status", out _));
        Assert.False(root.TryGetProperty("error_code", out _));
        Assert.False(root.TryGetProperty("exception_kind", out _));
        Assert.False(root.TryGetProperty("tool_id", out _));
        Assert.False(root.TryGetProperty("tool_version", out _));
        Assert.False(root.TryGetProperty("verification_state", out _));
        Assert.False(root.TryGetProperty("endpoint_role", out _));
        Assert.False(root.TryGetProperty("endpoint_hash", out _));
    }

    [Fact]
    public void Serializer_emits_the_exact_bounded_action_proof_shape()
    {
        var evt = new DiagnosticEvent(
            EventId: "evt_action_1",
            Ts: 1720000000.0,
            Iso: "2026-08-22T00:00:00.000Z",
            Level: "info",
            Component: "host.agent",
            EventCode: DiagnosticCodes.ToolVerificationSucceeded,
            CorrelationId: "c_abcdef1234567890",
            DurationMs: 12.0,
            Status: "succeeded",
            ToolId: "files.write",
            ToolVersion: "1.0.0",
            VerificationState: "verified",
            EndpointRole: "default_render_multimedia",
            EndpointHash: "0123456789abcdef");

        using var document = JsonDocument.Parse(DiagnosticEventPolicy.Serialize(evt));
        var root = document.RootElement;
        var names = root.EnumerateObject().Select(property => property.Name).ToHashSet();

        Assert.Equal(
            new HashSet<string>
            {
                "event_id", "ts", "iso", "level", "component", "event_code",
                "correlation_id", "duration_ms", "status", "tool_id", "tool_version",
                "verification_state", "endpoint_role", "endpoint_hash",
            },
            names);
        Assert.Equal("files.write", root.GetProperty("tool_id").GetString());
        Assert.Equal("1.0.0", root.GetProperty("tool_version").GetString());
        Assert.Equal("verified", root.GetProperty("verification_state").GetString());
        Assert.Equal("default_render_multimedia", root.GetProperty("endpoint_role").GetString());
        Assert.Equal("0123456789abcdef", root.GetProperty("endpoint_hash").GetString());
    }

    [Theory]
    [InlineData("system volume")]
    [InlineData("system/volume")]
    [InlineData("Bearer_secret")]
    [InlineData("https://example.com")]
    [InlineData("C:\\Windows\\audio")]
    [InlineData("system@volume")]
    public void Sanitizer_drops_invalid_tool_ids(string value)
    {
        var sanitized = DiagnosticEventPolicy.Sanitize(ActionEvent(ToolId: value));

        Assert.Null(sanitized.ToolId);
    }

    [Fact]
    public void Sanitizer_drops_overlong_tool_id()
    {
        var sanitized = DiagnosticEventPolicy.Sanitize(ActionEvent(ToolId: new string('x', 129)));

        Assert.Null(sanitized.ToolId);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("Bearer")]
    public void Sanitizer_drops_invalid_tool_versions(string value)
    {
        var sanitized = DiagnosticEventPolicy.Sanitize(ActionEvent(ToolVersion: value));

        Assert.Null(sanitized.ToolVersion);
    }

    [Theory]
    [InlineData("UNKNOWN")]
    [InlineData("verified now")]
    [InlineData("Bearer")]
    public void Sanitizer_drops_unknown_verification_states(string value)
    {
        var sanitized = DiagnosticEventPolicy.Sanitize(ActionEvent(VerificationState: value));

        Assert.Null(sanitized.VerificationState);
    }

    [Theory]
    [InlineData("communications")]
    [InlineData("default_render_console")]
    [InlineData("default render multimedia")]
    public void Sanitizer_drops_unknown_endpoint_roles(string value)
    {
        var sanitized = DiagnosticEventPolicy.Sanitize(ActionEvent(EndpointRole: value));

        Assert.Null(sanitized.EndpointRole);
    }

    [Theory]
    [InlineData("0123456789ABCDEf")]
    [InlineData("0123456789abcdeg")]
    [InlineData("0123456789abcde")]
    [InlineData("0123456789abcdef0")]
    [InlineData("Bearer0123456789")]
    public void Sanitizer_drops_invalid_endpoint_hashes(string value)
    {
        var sanitized = DiagnosticEventPolicy.Sanitize(ActionEvent(EndpointHash: value));

        Assert.Null(sanitized.EndpointHash);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("approval_required")]
    [InlineData("timed_out")]
    [InlineData("timeout")]
    public void Action_and_legacy_terminal_statuses_are_preserved(string status)
    {
        var sanitized = DiagnosticEventPolicy.Sanitize(ActionEvent(Status: status));

        Assert.Equal(status, sanitized.Status);
    }

    [Fact]
    public void Sanitizer_sanitizes_unapproved_values_and_clamps_lengths()
    {
        var evt = new DiagnosticEvent(
            EventId: "",
            Ts: 0,
            Iso: "",
            Level: "UNKNOWN_DANGEROUS_LEVEL",
            Component: "unapproved_comp_with_url_http://secret.com",
            EventCode: "UNAPPROVED!CODE@#$",
            CorrelationId: "invalid correlation id with spaces and secrets: Bearer 12345",
            DurationMs: -50.0,
            Status: "INVALID_STATUS",
            ErrorCode: "error with secret https://api.openai.com/v1/key=sk-1234567890abcdef",
            ExceptionKind: "weird_custom_exception_with_stacktrace_at_line_42");

        var sanitized = DiagnosticEventPolicy.Sanitize(evt);

        Assert.False(string.IsNullOrWhiteSpace(sanitized.EventId));
        Assert.True(sanitized.Ts > 0);
        Assert.False(string.IsNullOrWhiteSpace(sanitized.Iso));
        Assert.Equal("info", sanitized.Level);
        Assert.Equal("host", sanitized.Component);
        Assert.Equal("diagnostic_unclassified", sanitized.EventCode);
        Assert.StartsWith("c_", sanitized.CorrelationId!);
        Assert.Null(sanitized.DurationMs);
        Assert.Null(sanitized.Status);
        Assert.Null(sanitized.ErrorCode);
        Assert.Equal("unknown", sanitized.ExceptionKind);

        var json = DiagnosticEventPolicy.Serialize(sanitized);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stacktrace", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserializer_tolerates_raw_json_and_filters_unknown_properties()
    {
        var rawJson = """
        {
            "event_id": "evt_abc",
            "ts": 1720000000.1,
            "iso": "2026-08-20T00:00:00.100Z",
            "level": "error",
            "component": "worker.vts",
            "event_code": "vts_unavailable",
            "message": "Dangerous raw log message with secret sk-12345 and stacktrace",
            "stack_trace": "at Foo.Bar() in C:\\secret\\file.cs:line 10",
            "extra_field": 123
        }
        """;

        var parsed = DiagnosticEventPolicy.Deserialize(rawJson);
        Assert.NotNull(parsed);
        Assert.Equal("evt_abc", parsed.EventId);
        Assert.Equal("error", parsed.Level);
        Assert.Equal("worker.vts", parsed.Component);
        Assert.Equal("vts_unavailable", parsed.EventCode);

        var reSerialized = DiagnosticEventPolicy.Serialize(parsed);
        Assert.DoesNotContain("Dangerous", reSerialized);
        Assert.DoesNotContain("message", reSerialized);
        Assert.DoesNotContain("stack_trace", reSerialized);
        Assert.DoesNotContain("extra_field", reSerialized);
    }

    [Fact]
    public void Deserializer_drops_every_private_action_field()
    {
        var forbidden = new[]
        {
            "arguments", "output", "muted", "prompt", "reply", "path", "script",
            "stdout", "stderr", "device_name", "exception_message",
        };
        var injected = string.Join(",", forbidden.Select(name => $"\"{name}\":\"Bearer C:\\\\secret https://example.com\""));
        var raw = $$"""
        {
          "event_id":"evt_private",
          "ts":1720000000.0,
          "iso":"2026-08-22T00:00:00.000Z",
          "level":"info",
          "component":"host.agent",
          "event_code":"tool.requested",
          {{injected}}
        }
        """;

        var parsed = Assert.IsType<DiagnosticEvent>(DiagnosticEventPolicy.Deserialize(raw));
        using var document = JsonDocument.Parse(DiagnosticEventPolicy.Serialize(parsed));

        Assert.All(forbidden, name => Assert.False(document.RootElement.TryGetProperty(name, out _)));
        Assert.DoesNotContain("Bearer", document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserializer_keeps_the_event_but_drops_non_string_action_proof_values()
    {
        var raw = """
        {
          "event_id":"evt_invalid_types",
          "ts":1720000000.0,
          "iso":"2026-08-22T00:00:00.000Z",
          "level":"info",
          "component":"host.agent",
          "event_code":"tool.receipt.rejected",
          "tool_id":42,
          "tool_version":true,
          "verification_state":[],
          "endpoint_role":{},
          "endpoint_hash":42
        }
        """;

        var parsed = Assert.IsType<DiagnosticEvent>(DiagnosticEventPolicy.Deserialize(raw));

        Assert.Null(parsed.ToolId);
        Assert.Null(parsed.ToolVersion);
        Assert.Null(parsed.VerificationState);
        Assert.Null(parsed.EndpointRole);
        Assert.Null(parsed.EndpointHash);
    }

    [Fact]
    public void Standard_diagnostic_event_codes_are_accepted_by_policy()
    {
        var evt = new DiagnosticEvent(
            EventId: "evt_code_test",
            Ts: 1000,
            Iso: "2026-08-20T00:00:00Z",
            Level: "info",
            Component: "host.conversation",
            EventCode: DiagnosticCodes.TurnStart,
            CorrelationId: "c_1234567890abcdef",
            Status: "started");

        var sanitized = DiagnosticEventPolicy.Sanitize(evt);
        Assert.Equal(DiagnosticCodes.TurnStart, sanitized.EventCode);
        Assert.Equal("host.conversation", sanitized.Component);
        Assert.Equal("started", sanitized.Status);
    }

    private static DiagnosticEvent ActionEvent(
        string ToolId = "files.write",
        string ToolVersion = "1.0.0",
        string VerificationState = "verified",
        string EndpointRole = "default_render_multimedia",
        string EndpointHash = "0123456789abcdef",
        string Status = "succeeded") => new(
            EventId: "evt_action_test",
            Ts: 1720000000.0,
            Iso: "2026-08-22T00:00:00.000Z",
            Level: "info",
            Component: "host.agent",
            EventCode: "tool.verification.succeeded",
            CorrelationId: "c_abcdef1234567890",
            Status: Status,
            ToolId: ToolId,
            ToolVersion: ToolVersion,
            VerificationState: VerificationState,
            EndpointRole: EndpointRole,
            EndpointHash: EndpointHash);
}
