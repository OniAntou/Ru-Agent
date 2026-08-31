using RuBackend.Diagnostics;

namespace RuBackend.Tests.Fakes;

// For tests whose subject is not diagnostics: no background file writer or I/O.
internal sealed class DiscardingDiagnosticEmitter : IDiagnosticEmitter
{
    public bool TryEmit(
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
        string? endpointHash = null) => true;
}
