using System.Diagnostics;
using System.Text.Json.Nodes;
using RuAgent.Protocol;

namespace RuAgent.Broker.Diagnostics;

/// <summary>
/// OpenTelemetry seam for tool execution. Runs parallel to the JSONL
/// diagnostics and never replaces them: without a listener every helper is a
/// no-op. The Host owns the tracer provider; the execution authority only
/// produces spans on <see cref="Source"/>. Privacy: only bounded codes and the
/// tool identity are recorded. Arguments, outputs, summaries, paths and
/// payloads must never become tags.
/// </summary>
public static class BrokerTelemetry
{
    public const string SourceName = "RuAgent.Broker";

    /// <summary>Set when OTLP export is enabled. Unset keeps traces in-process only.</summary>
    public const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>Starts a <c>tool.execute</c> span, or null without a listener.</summary>
    public static Activity? StartToolActivity() =>
        Source.StartActivity("tool.execute", ActivityKind.Internal);

    /// <summary>
    /// Reads the tool id from an envelope payload. Returns null unless it is a
    /// short token, so malformed payloads cannot smuggle values into traces.
    /// </summary>
    public static string? ReadToolId(JsonObject? payload)
    {
        var toolId = payload?["tool_id"] is JsonValue value
            && value.TryGetValue<string>(out var text)
            ? text
            : null;
        return IsSafeDiagnosticToken(toolId) ? toolId : null;
    }

    /// <summary>Records the tool outcome using bounded codes only.</summary>
    public static void SetToolOutcome(Activity? activity, string? toolId, ToolResult? result)
    {
        if (activity is null || result is null)
        {
            return;
        }

        if (IsSafeDiagnosticToken(toolId))
        {
            activity.SetTag("tool.id", toolId);
        }

        if (ToolResultStatuses.Known.Contains(result.Status))
        {
            activity.SetTag("tool.status", result.Status);
            activity.SetStatus(result.Status == ToolResultStatuses.Succeeded
                ? ActivityStatusCode.Ok
                : ActivityStatusCode.Error);
        }

        if (IsSafeDiagnosticToken(result.ErrorCode))
        {
            activity.SetTag("error.code", result.ErrorCode);
        }

        if (ToolVerificationStates.Known.Contains(result.VerificationState))
        {
            activity.SetTag("verification.state", result.VerificationState);
        }
    }

    /// <summary>Token allowlist for trace tags: short, no whitespace or control chars.</summary>
    public static bool IsSafeDiagnosticToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        foreach (var ch in value)
        {
            var ok = (ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9')
                || ch is '_' or '-' or '.';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
