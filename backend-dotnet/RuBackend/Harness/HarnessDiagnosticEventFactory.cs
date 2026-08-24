using RuAgent.Harness.Core;
using RuBackend.Diagnostics;

namespace RuBackend.Harness;

public static class HarnessDiagnosticEventFactory
{
    private static readonly HashSet<string> AllowedHarnessCodes = new(StringComparer.Ordinal)
    {
        DiagnosticCodes.HarnessRunCreated,
        DiagnosticCodes.HarnessRunStateChanged,
        DiagnosticCodes.HarnessRunFailed,
        DiagnosticCodes.HarnessModelStepStarted,
        DiagnosticCodes.HarnessModelStepCompleted,
        DiagnosticCodes.HarnessToolProposed,
        DiagnosticCodes.HarnessToolExecutionRequested,
        DiagnosticCodes.HarnessToolExecutionCompleted,
        DiagnosticCodes.HarnessApprovalRequested,
        DiagnosticCodes.HarnessContinueRequested,
        DiagnosticCodes.HarnessReconciliationRequired,
        DiagnosticCodes.HarnessTransientFramesDropped,
        DiagnosticCodes.HarnessTrustDecided,
        DiagnosticCodes.HarnessWorkspaceBusy,
        DiagnosticCodes.HarnessIdempotencyConflict,
        DiagnosticCodes.HarnessPresetMismatch,
        DiagnosticCodes.HarnessTrustStale,
        DiagnosticCodes.HarnessPhaseNotAvailable,
        DiagnosticCodes.HarnessCursorMismatch,
    };

    // Only safe identifiers and bounded metrics are accepted. Raw prompt, command, path, code, diff, output, provider body, env, exception messages have no parameter.
    public static DiagnosticEvent Create(
        string eventCode,
        RunId? runId = null,
        ModelStepId? modelStepId = null,
        ToolCallId? toolCallId = null,
        ExecutionId? executionId = null,
        double? durationMs = null,
        long? byteCount = null,
        bool? truncated = null,
        string? status = null,
        string? errorCode = null)
    {
        if (!AllowedHarnessCodes.Contains(eventCode))
        {
            eventCode = DiagnosticCodes.Unclassified;
        }

        // Ensure only bounded safe codes/status/errorCode pass policy via sanitization later, but filter here to stable values.
        status = SanitizeStatus(status);
        errorCode = SanitizeErrorCode(errorCode);

        return new DiagnosticEvent(
            EventId: $"evt_{Guid.NewGuid():N}",
            Ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            Iso: DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Level: "info",
            Component: "host.harness",
            EventCode: eventCode,
            CorrelationId: CorrelationContext.GenerateId(),
            DurationMs: SanitizeDuration(durationMs),
            Status: status,
            ErrorCode: errorCode,
            ExceptionKind: null,
            ToolId: null,
            ToolVersion: null,
            VerificationState: null,
            EndpointRole: null,
            EndpointHash: null,
            RunId: runId?.Value,
            ModelStepId: modelStepId?.Value,
            ToolCallId: toolCallId?.Value,
            ExecutionId: executionId?.Value,
            ByteCount: SanitizeByteCount(byteCount),
            Truncated: truncated);
    }

    private static string? SanitizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var normalized = status.Trim().ToLowerInvariant();
        return normalized switch
        {
            "started" or "succeeded" or "failed" or "cancelled" or "denied" or "approval_required" or "timed_out" or "dropped" => normalized,
            _ => null
        };
    }

    private static string? SanitizeErrorCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var normalized = code.Trim().ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(normalized, "^[a-z0-9_.-]{1,64}$") ? normalized : null;
    }

    private static double? SanitizeDuration(double? durationMs)
    {
        if (durationMs is null)
        {
            return null;
        }

        if (durationMs is >= 0 and <= 86400000)
        {
            return Math.Round(durationMs.Value, 2);
        }

        return null;
    }

    private static long? SanitizeByteCount(long? byteCount)
    {
        if (byteCount is null)
        {
            return null;
        }

        if (byteCount is >= 0 and <= 16777216)
        {
            return byteCount;
        }

        return null;
    }
}
