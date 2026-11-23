using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RuBackend.Diagnostics;

/// <summary>
/// Bounded identity of a failure for both the diagnostic log and the run's terminal
/// state: the exception's kind bucket and the throwing type and method, never its
/// message or stack. One implementation keeps the two surfaces from drifting, and a
/// site that does not look like a type and method is dropped rather than persisted.
/// </summary>
public static class ExceptionFingerprint
{
    private static readonly Regex SafeSitePattern = new("^[A-Za-z0-9_.`<>+]{1,160}$", RegexOptions.Compiled);

    /// <summary>The run's terminal fingerprint, or null when the exception cannot name itself.</summary>
    public static RuAgent.Harness.Core.RunFailureFingerprint? For(Exception? exception) =>
        exception is null
            ? null
            : new RuAgent.Harness.Core.RunFailureFingerprint(KindOf(exception), SiteOf(exception));

    /// <summary>Only the exception's type bucket is recorded, never its message or stack.</summary>
    public static string KindOf(Exception? exception)
    {
        if (exception is null)
        {
            return "unknown";
        }

        return exception switch
        {
            // The exception's own code is the most precise bucket when it carries one.
            RuAgent.Harness.Persistence.HarnessStoreException => "store",
            System.Data.Common.DbException => "store",
            RuAgent.Protocol.AgentProtocolException => "protocol",
            RuAgent.Harness.Core.ToolTransportException => "transport",
            TimeoutException => "timeout",
            UnauthorizedAccessException => "unauthorized",
            HttpRequestException or System.Net.Sockets.SocketException => "network",
            IOException => "io",
            OperationCanceledException => "cancelled",
            ArgumentOutOfRangeException => "out_of_range",
            ArgumentException => "invalid_argument",
            ObjectDisposedException => "object_disposed",
            InvalidOperationException => "invalid_operation",
            InvalidDataException or FormatException or System.Text.Json.JsonException => "invalid_data",
            NotSupportedException => "not_supported",
            KeyNotFoundException => "key_not_found",
            NullReferenceException => "null_reference",
            _ => "unknown",
        };
    }

    /// <summary>The throwing method's type and name only: enough to find a failure without a message.</summary>
    public static string? SiteOf(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        try
        {
            var method = new StackTrace(exception, false).GetFrame(0)?.GetMethod();
            if (method?.DeclaringType?.FullName is not { } type)
            {
                return null;
            }

            var site = $"{type}.{method.Name}";
            return SafeSitePattern.IsMatch(site) ? site : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            // A stack trace is diagnostic decoration; a failure to read it must not
            // change the failure.
            return null;
        }
    }
}
