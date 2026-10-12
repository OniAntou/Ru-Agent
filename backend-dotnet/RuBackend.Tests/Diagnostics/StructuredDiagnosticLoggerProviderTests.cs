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

    /// <summary>
    /// The agent's own loop service used to be stamped <c>worker.unavailable</c> purely because its
    /// category contains "runtime", so a provider 4xx logged as worker.unavailable and sent the
    /// reader after an outage that cannot happen. A category with no code of its own is
    /// unclassified instead.
    /// </summary>
    [Theory]
    [InlineData("RuBackend.Harness.AgentRuntimeLoopService")]
    [InlineData("RuBackend.Harness.AgentRuntime")]
    [InlineData("RuBackend.Harness.RuntimeDrivenService")]
    public void Runtime_category_without_an_explicit_code_is_not_reported_as_worker_unavailable(string category)
    {
        var emitter = new RecordingEmitter();
        using var provider = new StructuredDiagnosticLoggerProvider(emitter);
        var logger = provider.CreateLogger(category);

        logger.LogError(new InvalidOperationException("secret payload"), "raw failure");

        var call = Assert.Single(emitter.Calls);
        Assert.Equal(DiagnosticCodes.Unclassified, call.EventCode);
    }

    /// <summary>The narrowing keeps the worker categories: those really are worker failures.</summary>
    [Fact]
    public void Worker_category_still_reports_worker_unavailable()
    {
        var emitter = new RecordingEmitter();
        using var provider = new StructuredDiagnosticLoggerProvider(emitter);
        var logger = provider.CreateLogger("RuBackend.Harness.Worker.Loop");

        logger.LogError(new InvalidOperationException("secret payload"), "raw failure");

        var call = Assert.Single(emitter.Calls);
        Assert.Equal(DiagnosticCodes.WorkerUnavailable, call.EventCode);
    }

    [Fact]
    public void An_explicit_event_code_still_wins_over_the_category()
    {
        var emitter = new RecordingEmitter();
        using var provider = new StructuredDiagnosticLoggerProvider(emitter);
        var logger = provider.CreateLogger("RuBackend.Harness.AgentRuntimeLoopService");

        logger.LogWarning("{ru_event_code}", "provider.payment_required");

        var call = Assert.Single(emitter.Calls);
        Assert.Equal("provider.payment_required", call.EventCode);
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
