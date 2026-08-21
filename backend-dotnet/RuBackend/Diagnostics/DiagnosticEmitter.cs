namespace RuBackend.Diagnostics;

public sealed class DiagnosticEmitter : IDiagnosticEmitter
{
    private readonly StructuredDiagnosticLogWriter _writer;
    private readonly TimeProvider _clock;

    public DiagnosticEmitter(
        StructuredDiagnosticLogWriter writer,
        TimeProvider? clock = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _clock = clock ?? TimeProvider.System;
    }

    public bool TryEmit(
        string level,
        string component,
        string eventCode,
        string? correlationId = null,
        double? durationMs = null,
        string? status = null,
        string? errorCode = null,
        string? exceptionKind = null)
    {
        try
        {
            var now = _clock.GetUtcNow();
            var evt = DiagnosticEventPolicy.Sanitize(new DiagnosticEvent(
                $"evt_{Guid.NewGuid():N}",
                now.ToUnixTimeMilliseconds() / 1000.0,
                now.ToString("O"),
                level,
                component,
                eventCode,
                correlationId ?? CorrelationContext.Current,
                durationMs,
                status,
                errorCode,
                exceptionKind));
            return _writer.TryEnqueue(evt);
        }
        catch
        {
            // Diagnostics must never change the user operation result.
            return false;
        }
    }
}
