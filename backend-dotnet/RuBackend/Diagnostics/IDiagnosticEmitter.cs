namespace RuBackend.Diagnostics;

public interface IDiagnosticEmitter
{
    bool TryEmit(
        string level,
        string component,
        string eventCode,
        string? correlationId = null,
        double? durationMs = null,
        string? status = null,
        string? errorCode = null,
        string? exceptionKind = null,
        string? toolId = null,
        string? toolVersion = null,
        string? verificationState = null,
        string? endpointRole = null,
        string? endpointHash = null);
}
