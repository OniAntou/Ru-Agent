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

    private const int MaxUnwrapDepth = 4;
    private const int MaxScannedFrames = 64;
    private static readonly Regex AsyncStateMachinePattern = new(@"^<([A-Za-z0-9_]+)>d__\d+$", RegexOptions.Compiled);

    /// <summary>The run's terminal fingerprint, or null when the exception cannot name itself.</summary>
    public static RuAgent.Harness.Core.RunFailureFingerprint? For(Exception? exception) =>
        exception is null
            ? null
            : new RuAgent.Harness.Core.RunFailureFingerprint(KindOf(exception), SiteOf(exception));

    /// <summary>
    /// The fault a wrapper carries: an <see cref="AggregateException"/> holding exactly one
    /// fault, or a <see cref="System.Reflection.TargetInvocationException"/>, says nothing
    /// itself, so classification looks at what it wraps. Bounded, and a wrapper holding
    /// several faults stays as it is.
    /// </summary>
    public static Exception Unwrap(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var current = exception;
        for (var depth = 0; depth < MaxUnwrapDepth; depth++)
        {
            Exception? inner = current switch
            {
                AggregateException { InnerExceptions.Count: 1 } aggregate => aggregate.InnerExceptions[0],
                System.Reflection.TargetInvocationException { InnerException: { } wrapped } => wrapped,
                _ => null,
            };
            if (inner is null)
            {
                break;
            }

            current = inner;
        }

        return current;
    }

    /// <summary>Only the exception's type bucket is recorded, never its message or stack.</summary>
    public static string KindOf(Exception? exception)
    {
        exception = exception is null ? null : Unwrap(exception);
        if (exception is null)
        {
            return "unknown";
        }

        return exception switch
        {
            // The exception's own code is the most precise bucket when it carries one.
            RuAgent.Harness.Core.AgentRunStateException => "state",
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

    /// <summary>
    /// The type and method name only: enough to find a failure without a message. The
    /// innermost Ru frame is preferred over the throwing frame, because a fault raised
    /// by a framework helper (a SQLite error code, a throw helper) would otherwise name
    /// only the helper. An async method is named as written, not as its state machine.
    /// </summary>
    public static string? SiteOf(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        exception = Unwrap(exception);
        try
        {
            var trace = new StackTrace(exception, false);
            System.Reflection.MethodBase? chosen = null;
            var count = Math.Min(trace.FrameCount, MaxScannedFrames);
            for (var index = 0; index < count && chosen is null; index++)
            {
                var method = trace.GetFrame(index)?.GetMethod();
                if (method?.DeclaringType?.FullName is { } name && IsRuCode(name))
                {
                    chosen = method;
                }
            }

            chosen ??= trace.GetFrame(0)?.GetMethod();
            if (Describe(chosen) is not { } site)
            {
                return null;
            }

            return SafeSitePattern.IsMatch(site) ? site : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            // A stack trace is diagnostic decoration; a failure to read it must not
            // change the failure.
            return null;
        }
    }

    private static bool IsRuCode(string typeName) =>
        typeName.StartsWith("RuBackend.", StringComparison.Ordinal)
        || typeName.StartsWith("RuAgent.", StringComparison.Ordinal);

    /// <summary>"Type.Method", with a compiler async state machine folded back to its method.</summary>
    private static string? Describe(System.Reflection.MethodBase? method)
    {
        if (method?.DeclaringType is not { FullName: { } type } declaring)
        {
            return null;
        }

        if (method.Name == "MoveNext"
            && declaring.DeclaringType?.FullName is { } outer
            && AsyncStateMachinePattern.Match(declaring.Name) is { Success: true } match)
        {
            return $"{outer}.{match.Groups[1].Value}";
        }

        return $"{type}.{method.Name}";
    }
}
