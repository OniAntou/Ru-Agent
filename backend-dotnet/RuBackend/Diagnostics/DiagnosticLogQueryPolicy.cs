using System.Globalization;
using System.Text.RegularExpressions;

namespace RuBackend.Diagnostics;

public sealed class DiagnosticQueryValidationException : Exception
{
    public DiagnosticQueryValidationException(string errorCode)
        : base(errorCode)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public static class DiagnosticLogQueryPolicy
{
    public const int MaxQueryLimit = 5_000;
    public const int MaxFilterLength = 128;
    public const int MaxSinceLength = 64;
    public const int MaxCursorLength = 256;
    public const int MaxRecordBytes = 256_000;
    public const int MaxScanBytesPerFile = 4_000_000;
    public const int MaxExportBytes = 2_000_000;

    private static readonly Regex SafeFilterPattern = new(
        "^[a-zA-Z0-9_.-]{1,128}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "trace", "debug", "info", "warning", "error", "critical",
    };

    public static void Validate(DiagnosticLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit is < 1 or > MaxQueryLimit)
        {
            throw new DiagnosticQueryValidationException("diagnostic_limit_out_of_range");
        }

        ValidateFilter(query.Level, allowAnySafeCode: false);
        ValidateFilter(query.Component, allowAnySafeCode: true);
        ValidateFilter(query.EventCode, allowAnySafeCode: true);

        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            if (query.CorrelationId!.Length > MaxFilterLength ||
                !CorrelationContext.IsValid(query.CorrelationId.Trim()))
            {
                throw new DiagnosticQueryValidationException("diagnostic_correlation_invalid");
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Since))
        {
            if (query.Since!.Length > MaxSinceLength || !TryParseSince(query.Since, out _, out _))
            {
                throw new DiagnosticQueryValidationException("diagnostic_since_invalid");
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (query.Cursor!.Length > MaxCursorLength || !TryParseCursor(query.Cursor, out _, out _))
            {
                throw new DiagnosticQueryValidationException("diagnostic_cursor_invalid");
            }
        }
    }

    public static bool TryParseSince(
        string raw,
        out double timestamp,
        out DateOnly date)
    {
        timestamp = 0;
        date = default;
        var value = raw.Trim();

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var parsedDateTime))
        {
            timestamp = parsedDateTime.ToUnixTimeMilliseconds() / 1000.0;
            if (timestamp < 0)
            {
                timestamp = 0;
                return false;
            }
            date = DateOnly.FromDateTime(parsedDateTime.UtcDateTime);
            return true;
        }

        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedNumber) ||
            !double.IsFinite(parsedNumber) ||
            parsedNumber < 0 ||
            parsedNumber > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        timestamp = parsedNumber;
        date = DateOnly.FromDateTime(
            DateTimeOffset.FromUnixTimeSeconds((long)parsedNumber).UtcDateTime);
        return true;
    }

    public static bool TryParseCursor(
        string raw,
        out double timestamp,
        out string? eventId)
    {
        timestamp = 0;
        eventId = null;
        var parts = raw.Trim().Split('_', 3);
        if (parts.Length != 3 ||
            !string.Equals(parts[0], "cur", StringComparison.OrdinalIgnoreCase) ||
            !double.TryParse(
                parts[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out timestamp) ||
            !double.IsFinite(timestamp) ||
            timestamp < 0 ||
            !SafeFilterPattern.IsMatch(parts[2]))
        {
            timestamp = 0;
            return false;
        }

        eventId = parts[2];
        return true;
    }

    private static void ValidateFilter(string? value, bool allowAnySafeCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxFilterLength)
        {
            throw new DiagnosticQueryValidationException("diagnostic_filter_too_long");
        }

        if (!allowAnySafeCode && !AllowedLevels.Contains(trimmed))
        {
            throw new DiagnosticQueryValidationException("diagnostic_level_invalid");
        }

        if (allowAnySafeCode && !SafeFilterPattern.IsMatch(trimmed))
        {
            throw new DiagnosticQueryValidationException("diagnostic_filter_invalid");
        }
    }
}
