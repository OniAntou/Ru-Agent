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
        "started", "succeeded", "failed", "skipped", "timeout", "cancelled", "dropped"
    };

    private static readonly HashSet<string> AllowedExceptionKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "timeout", "network", "io", "invalid_argument", "unauthorized", "cancelled", "unknown"
    };

    private static readonly Regex SafeCodePattern = new("^[a-z0-9_.-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex SafeComponentPattern = new("^[a-z0-9_.-]{1,64}$", RegexOptions.Compiled);

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

            var eventId = root.TryGetProperty("event_id", out var pEventId) ? pEventId.GetString() : null;
            var ts = root.TryGetProperty("ts", out var pTs) && pTs.TryGetDouble(out var vTs) ? vTs : 0;
            var iso = root.TryGetProperty("iso", out var pIso) ? pIso.GetString() : null;
            var level = root.TryGetProperty("level", out var pLevel) ? pLevel.GetString() : null;
            var component = root.TryGetProperty("component", out var pComponent) ? pComponent.GetString() : null;
            var eventCode = root.TryGetProperty("event_code", out var pEventCode) ? pEventCode.GetString() : null;
            var correlationId = root.TryGetProperty("correlation_id", out var pCorr) ? pCorr.GetString() : null;
            var durationMs = root.TryGetProperty("duration_ms", out var pDur) && pDur.TryGetDouble(out var vDur) ? (double?)vDur : null;
            var status = root.TryGetProperty("status", out var pStat) ? pStat.GetString() : null;
            var errorCode = root.TryGetProperty("error_code", out var pErr) ? pErr.GetString() : null;
            var exceptionKind = root.TryGetProperty("exception_kind", out var pExc) ? pExc.GetString() : null;

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
                exceptionKind);

            return Sanitize(raw);
        }
        catch (JsonException)
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

        var eventCode = !string.IsNullOrWhiteSpace(evt.EventCode) && SafeCodePattern.IsMatch(evt.EventCode.Trim().ToLowerInvariant())
            ? evt.EventCode.Trim().ToLowerInvariant()
            : "diagnostic_unclassified";

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
            exceptionKind);
    }
}
