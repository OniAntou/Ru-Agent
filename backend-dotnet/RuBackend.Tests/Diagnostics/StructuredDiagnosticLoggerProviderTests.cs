using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using RuBackend.Diagnostics;
using Xunit;

namespace RuBackend.Tests.Diagnostics;

public sealed class StructuredDiagnosticLoggerProviderTests
{
    [Fact]
    public void Production_host_owns_only_the_structured_logger_provider()
    {
        using var factory = new BackendWebApplicationFactory();
        _ = factory.CreateClient();

        var providers = factory.Services.GetServices<ILoggerProvider>().ToArray();

        Assert.IsType<StructuredDiagnosticLoggerProvider>(Assert.Single(providers));
    }

    [Fact]
    public void Logger_persists_metadata_without_formatting_message_or_exception_text()
    {
        var emitter = new RecordingEmitter();
        using var provider = new StructuredDiagnosticLoggerProvider(emitter);
        var logger = provider.CreateLogger("RuBackend.Test");

        logger.LogError(
            new InvalidOperationException("secret prompt and stack trace"),
            "raw body {Body}",
            "user text");

        var call = Assert.Single(emitter.Calls);
        Assert.Equal("error", call.Level);
        Assert.Equal("RuBackend.Test", call.Component);
        Assert.Equal("diagnostic_unclassified", call.EventCode);
        Assert.Equal("unknown", call.ExceptionKind);
        Assert.Null(call.ErrorCode);
    }

    [Fact]
    public void Known_runtime_categories_receive_stable_failure_event_codes()
    {
        var emitter = new RecordingEmitter();
        using var provider = new StructuredDiagnosticLoggerProvider(emitter);
        var logger = provider.CreateLogger("RuBackend.Memory");

        logger.LogWarning(new HttpRequestException("secret payload"), "raw failure");

        var call = Assert.Single(emitter.Calls);
        Assert.Equal("memory.failed", call.EventCode);
        Assert.Equal("network", call.ExceptionKind);
    }

    private sealed class RecordingEmitter : IDiagnosticEmitter
    {
        public List<Call> Calls { get; } = [];

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
            string? endpointHash = null)
        {
            Calls.Add(new Call(level, component, eventCode, errorCode, exceptionKind));
            return true;
        }

        public sealed record Call(
            string Level,
            string Component,
            string EventCode,
            string? ErrorCode,
            string? ExceptionKind);
    }
}
