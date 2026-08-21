namespace RuBackend.Diagnostics;

using System.Collections;
using System.Globalization;
using Microsoft.Extensions.Logging;

public sealed class StructuredDiagnosticLoggerProvider : ILoggerProvider
{
    private readonly IDiagnosticEmitter _emitter;

    public StructuredDiagnosticLoggerProvider(IDiagnosticEmitter emitter)
    {
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
    }

    public ILogger CreateLogger(string categoryName) => new StructuredDiagnosticLogger(_emitter, categoryName);

    public void Dispose()
    {
    }

    private sealed class StructuredDiagnosticLogger : ILogger
    {
        private readonly IDiagnosticEmitter _emitter;
        private readonly string _categoryName;

        public StructuredDiagnosticLogger(IDiagnosticEmitter emitter, string categoryName)
        {
            _emitter = emitter;
            _categoryName = categoryName;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var fields = ReadFields(state);
            var exceptionKind = fields.TryGetValue("exception_kind", out var fieldKind)
                ? fieldKind
                : ClassifyException(exception);
            _emitter.TryEmit(
                MapLevel(logLevel),
                _categoryName,
                fields.TryGetValue("ru_event_code", out var eventCode)
                    ? eventCode
                    : InferEventCode(_categoryName),
                durationMs: ParseDuration(fields),
                status: fields.TryGetValue("status", out var status) ? status : null,
                errorCode: fields.TryGetValue("error_code", out var errorCode) ? errorCode : null,
                exceptionKind: exceptionKind);
        }

        private static Dictionary<string, string> ReadFields<TState>(TState state)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (state is not IEnumerable pairs)
            {
                return result;
            }

            foreach (var item in pairs)
            {
                if (item is not KeyValuePair<string, object?> pair)
                {
                    continue;
                }

                var key = pair.Key.Trim();
                if (key is not ("ru_event_code" or "status" or "error_code" or "exception_kind" or "duration_ms"))
                {
                    continue;
                }

                if (pair.Value is string value)
                {
                    result[key] = value;
                }
                else if (pair.Value is IFormattable formattable)
                {
                    result[key] = formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }

            return result;
        }

        private static double? ParseDuration(IReadOnlyDictionary<string, string> fields)
        {
            return fields.TryGetValue("duration_ms", out var raw)
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static string ClassifyException(Exception? exception)
        {
            if (exception is null)
            {
                return "unknown";
            }

            return exception is TimeoutException
                ? "timeout"
                : exception is UnauthorizedAccessException
                    ? "unauthorized"
                    : exception is HttpRequestException or System.Net.Sockets.SocketException
                        ? "network"
                        : exception is IOException or FileNotFoundException or DirectoryNotFoundException
                            ? "io"
                            : exception is OperationCanceledException
                                ? "cancelled"
                                : "unknown";
        }

        private static string MapLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Warning => "warning",
            LogLevel.Error => "error",
            LogLevel.Critical => "critical",
            _ => "info",
        };

        private static string InferEventCode(string categoryName)
        {
            var category = categoryName.ToLowerInvariant();
            if (category.Contains(".stt", StringComparison.Ordinal))
            {
                return DiagnosticCodes.SttFailed;
            }

            if (category.Contains(".voice", StringComparison.Ordinal))
            {
                return DiagnosticCodes.SpeechSynthesizeFailed;
            }

            if (category.Contains(".vts", StringComparison.Ordinal))
            {
                return DiagnosticCodes.VtsFailed;
            }

            if (category.Contains(".observation", StringComparison.Ordinal))
            {
                return DiagnosticCodes.ObservationFailed;
            }

            if (category.Contains(".memory", StringComparison.Ordinal))
            {
                return DiagnosticCodes.MemoryFailed;
            }

            if (category.Contains(".business", StringComparison.Ordinal))
            {
                return DiagnosticCodes.TurnFailed;
            }

            if (category.Contains(".worker", StringComparison.Ordinal) ||
                category.Contains("runtime", StringComparison.Ordinal))
            {
                return DiagnosticCodes.WorkerUnavailable;
            }

            return DiagnosticCodes.Unclassified;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
