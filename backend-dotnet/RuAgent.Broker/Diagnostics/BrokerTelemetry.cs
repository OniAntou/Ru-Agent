using System.Diagnostics;
using System.Diagnostics.Metrics;
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

    /// <summary>
    /// Counters over the same source. They answer what a span list cannot answer
    /// cheaply: how often a tool fails, under which bounded code, and how often a
    /// file read repeats one this session already read.
    /// </summary>
    private static readonly Meter Meter = new(SourceName);

    private static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>(
        "ru.tool.calls",
        unit: "call",
        description: "Tool executions by bounded tool id, status and error code.");

    private static readonly Counter<long> ReadPages = Meter.CreateCounter<long>(
        "ru.tool.read_pages",
        unit: "page",
        description: "File read pages, split by whether the session already read that path.");

    /// <summary>Records one finished tool call. Unsafe tokens are dropped, never trimmed.</summary>
    public static void RecordToolCall(string? toolId, ToolResult? result)
    {
        if (result is null)
        {
            return;
        }

        var tags = new TagList();
        if (IsSafeDiagnosticToken(toolId))
        {
            tags.Add("tool.id", toolId);
        }

        if (ToolResultStatuses.Known.Contains(result.Status))
        {
            tags.Add("tool.status", result.Status);
        }

        if (IsSafeDiagnosticToken(result.ErrorCode))
        {
            tags.Add("error.code", result.ErrorCode);
        }

        ToolCalls.Add(1, tags);
    }

    /// <summary>Records one read page and whether this session had already read that path.</summary>
    public static void RecordReadPage(string? toolId, bool repeated)
    {
        var tags = new TagList { { "repeated", repeated ? "true" : "false" } };
        if (IsSafeDiagnosticToken(toolId))
        {
            tags.Add("tool.id", toolId);
        }

        ReadPages.Add(1, tags);
    }

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
