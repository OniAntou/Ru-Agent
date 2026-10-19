namespace RuBackend.Diagnostics;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public static class DiagnosticEventPolicy
{
    private static readonly HashSet<string> AllowedLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "trace", "debug", "info", "warning", "error", "critical"
    };

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "started", "succeeded", "failed", "skipped", "timeout", "cancelled", "dropped",
        "denied", "approval_required", "timed_out"
    };

    private static readonly HashSet<string> AllowedVerificationStates = new(StringComparer.Ordinal)
    {
        "not_required", "pending", "verified", "failed"
    };

    private static readonly HashSet<string> AllowedExceptionKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "timeout", "network", "io", "invalid_argument", "unauthorized", "cancelled", "unknown",
        "invalid_operation", "invalid_data", "not_supported", "key_not_found", "null_reference", "object_disposed", "out_of_range"
    };

    private static readonly Regex SafeCodePattern = new("^[a-z0-9_.-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex SafeComponentPattern = new("^[a-z0-9_.-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex SafeExceptionSitePattern = new("^[A-Za-z0-9_.`<>+]{1,160}$", RegexOptions.Compiled);
    private static readonly Regex SafeToolIdPattern = new("^[a-z0-9_.-]{1,128}$", RegexOptions.Compiled);
    private static readonly Regex SemanticVersionPattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$",
        RegexOptions.Compiled);
    private static readonly Regex EndpointHashPattern = new("^[0-9a-f]{16}$", RegexOptions.Compiled);
    private static readonly Regex RunIdPattern = new("^run_[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex ModelStepIdPattern = new("^step_[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex ToolCallIdPattern = new("^call_[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex ExecutionIdPattern = new("^exec_[0-9a-f]{32}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static string Serialize(DiagnosticEvent evt)
    {
        var sanitized = Sanitize(evt);
        return JsonSerializer.Serialize(sanitized, SerializerOptions);
    }

    public static DiagnosticEvent? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var eventId = ReadOptionalString(root, "event_id");
            var ts = root.TryGetProperty("ts", out var pTs)
                && pTs.ValueKind == JsonValueKind.Number
                && pTs.TryGetDouble(out var vTs) ? vTs : 0;
            var iso = ReadOptionalString(root, "iso");
            var level = ReadOptionalString(root, "level");
            var component = ReadOptionalString(root, "component");
            var eventCode = ReadOptionalString(root, "event_code");
            var correlationId = ReadOptionalString(root, "correlation_id");
            var durationMs = root.TryGetProperty("duration_ms", out var pDur)
                && pDur.ValueKind == JsonValueKind.Number
                && pDur.TryGetDouble(out var vDur) ? (double?)vDur : null;
            var status = ReadOptionalString(root, "status");
            var errorCode = ReadOptionalString(root, "error_code");
            var exceptionKind = ReadOptionalString(root, "exception_kind");
            var toolId = ReadOptionalString(root, "tool_id");
            var toolVersion = ReadOptionalString(root, "tool_version");
            var verificationState = ReadOptionalString(root, "verification_state");
            var endpointRole = ReadOptionalString(root, "endpoint_role");
            var endpointHash = ReadOptionalString(root, "endpoint_hash");
            var runId = ReadOptionalString(root, "run_id");
            var modelStepId = ReadOptionalString(root, "model_step_id");
            var toolCallId = ReadOptionalString(root, "tool_call_id");
            var executionId = ReadOptionalString(root, "execution_id");
            var byteCount = root.TryGetProperty("byte_count", out var pByte)
                && pByte.ValueKind == JsonValueKind.Number
                && pByte.TryGetInt64(out var vByte) ? (long?)vByte : null;
            var exceptionSite = ReadOptionalString(root, "exception_site");
            var truncated = root.TryGetProperty("truncated", out var pTrunc)
                && pTrunc.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? (bool?)pTrunc.GetBoolean() : null;

            var raw = new DiagnosticEvent(
                eventId ?? string.Empty,
                ts,
                iso ?? string.Empty,
                level ?? string.Empty,
                component ?? string.Empty,
                eventCode ?? string.Empty,
                correlationId,
                durationMs,
                status,
                errorCode,
                exceptionKind,
                toolId,
                toolVersion,
                verificationState,
                endpointRole,
                endpointHash,
                runId,
                modelStepId,
                toolCallId,
                executionId,
                byteCount,
                truncated,
                exceptionSite);

            return Sanitize(raw);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static DiagnosticEvent Sanitize(DiagnosticEvent evt)
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = !string.IsNullOrWhiteSpace(evt.EventId) && SafeCodePattern.IsMatch(evt.EventId.Trim().ToLowerInvariant())
            ? evt.EventId.Trim()
            : $"evt_{Guid.NewGuid():N}";

        var ts = evt.Ts > 0 ? evt.Ts : now.ToUnixTimeMilliseconds() / 1000.0;
        var iso = !string.IsNullOrWhiteSpace(evt.Iso) && evt.Iso.Contains('T', StringComparison.Ordinal)
            ? evt.Iso.Trim()
            : now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var level = !string.IsNullOrWhiteSpace(evt.Level) && AllowedLevels.Contains(evt.Level.Trim().ToLowerInvariant())
            ? evt.Level.Trim().ToLowerInvariant()
            : "info";

        var component = !string.IsNullOrWhiteSpace(evt.Component) && SafeComponentPattern.IsMatch(evt.Component.Trim().ToLowerInvariant())
            ? evt.Component.Trim().ToLowerInvariant()
            : "host";

        // The fallback code comes from the shared list. Writing the value here as well
        // would let a record this policy sanitizes stop being recognised by whoever
        // filters on DiagnosticCodes.Unclassified.
        var eventCode = !string.IsNullOrWhiteSpace(evt.EventCode) && SafeCodePattern.IsMatch(evt.EventCode.Trim().ToLowerInvariant())
            ? evt.EventCode.Trim().ToLowerInvariant()
            : DiagnosticCodes.Unclassified;

        string? correlationId = null;
        if (!string.IsNullOrWhiteSpace(evt.CorrelationId))
        {
            correlationId = CorrelationContext.IsValid(evt.CorrelationId)
                ? evt.CorrelationId.Trim()
                : CorrelationContext.GenerateId();
        }

        double? durationMs = null;
        if (evt.DurationMs is >= 0 and <= 86400000)
        {
            durationMs = Math.Round(evt.DurationMs.Value, 2);
        }

        string? status = null;
        if (!string.IsNullOrWhiteSpace(evt.Status) && AllowedStatuses.Contains(evt.Status.Trim().ToLowerInvariant()))
        {
            status = evt.Status.Trim().ToLowerInvariant();
        }

        string? errorCode = null;
        if (!string.IsNullOrWhiteSpace(evt.ErrorCode) && SafeCodePattern.IsMatch(evt.ErrorCode.Trim().ToLowerInvariant()))
        {
            errorCode = evt.ErrorCode.Trim().ToLowerInvariant();
        }

        string? exceptionKind = null;
        if (!string.IsNullOrWhiteSpace(evt.ExceptionKind))
        {
            var cleanedKind = evt.ExceptionKind.Trim().ToLowerInvariant();
            exceptionKind = AllowedExceptionKinds.Contains(cleanedKind) ? cleanedKind : "unknown";
        }

        string? toolId = null;
        if (!string.IsNullOrEmpty(evt.ToolId)
            && SafeToolIdPattern.IsMatch(evt.ToolId)
            && !evt.ToolId.Contains("bearer", StringComparison.OrdinalIgnoreCase))
        {
            toolId = evt.ToolId;
        }

        string? toolVersion = null;
        if (!string.IsNullOrEmpty(evt.ToolVersion) && SemanticVersionPattern.IsMatch(evt.ToolVersion))
        {
            toolVersion = evt.ToolVersion;
        }

        string? verificationState = null;
        if (!string.IsNullOrEmpty(evt.VerificationState)
            && AllowedVerificationStates.Contains(evt.VerificationState))
        {
            verificationState = evt.VerificationState;
        }

        var endpointRole = string.Equals(
            evt.EndpointRole,
            "default_render_multimedia",
            StringComparison.Ordinal)
                ? evt.EndpointRole
                : null;

        string? endpointHash = null;
        if (!string.IsNullOrEmpty(evt.EndpointHash) && EndpointHashPattern.IsMatch(evt.EndpointHash))
        {
            endpointHash = evt.EndpointHash;
        }

        string? runId = null;
        if (!string.IsNullOrEmpty(evt.RunId) && RunIdPattern.IsMatch(evt.RunId))
        {
            runId = evt.RunId;
        }

        string? modelStepId = null;
        if (!string.IsNullOrEmpty(evt.ModelStepId) && ModelStepIdPattern.IsMatch(evt.ModelStepId))
        {
            modelStepId = evt.ModelStepId;
        }

        string? toolCallId = null;
        if (!string.IsNullOrEmpty(evt.ToolCallId) && ToolCallIdPattern.IsMatch(evt.ToolCallId))
        {
            toolCallId = evt.ToolCallId;
        }

        string? executionId = null;
        if (!string.IsNullOrEmpty(evt.ExecutionId) && ExecutionIdPattern.IsMatch(evt.ExecutionId))
        {
            executionId = evt.ExecutionId;
        }

        long? byteCount = null;
        if (evt.ByteCount is >= 0 and <= 16777216)
        {
            byteCount = evt.ByteCount;
        }

        // Type and method only: this is where the exception was thrown, never its message or stack text.
        var exceptionSite = !string.IsNullOrEmpty(evt.ExceptionSite) && SafeExceptionSitePattern.IsMatch(evt.ExceptionSite)
            ? evt.ExceptionSite
            : null;

        bool? truncated = null;
        if (evt.Truncated is not null)
        {
            truncated = evt.Truncated;
        }

        return new DiagnosticEvent(
            eventId,
            ts,
            iso,
            level,
            component,
            eventCode,
            correlationId,
            durationMs,
            status,
            errorCode,
            exceptionKind,
            toolId,
            toolVersion,
            verificationState,
            endpointRole,
            endpointHash,
            runId,
            modelStepId,
            toolCallId,
            executionId,
            byteCount,
            truncated,
            exceptionSite);
    }

    private static string? ReadOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
