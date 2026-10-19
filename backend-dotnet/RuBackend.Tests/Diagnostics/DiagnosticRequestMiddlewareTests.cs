using Microsoft.AspNetCore.Http;
using RuBackend.Diagnostics;
using Xunit;

namespace RuBackend.Tests.Diagnostics;

public sealed class DiagnosticRequestMiddlewareTests
{
    [Fact]
    public async Task Middleware_emits_a_bounded_route_event_with_duration_and_status()
    {
        var emitter = new FakeDiagnosticEmitter();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/settings";

        var middleware = new DiagnosticRequestMiddleware(
            next: httpContext =>
            {
                httpContext.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            emitter);

        await middleware.InvokeAsync(context);

        Assert.Equal("info", emitter.Level);
        Assert.Equal("host.http", emitter.Component);
        Assert.Equal("http.business", emitter.EventCode);
        Assert.Equal("succeeded", emitter.Status);
        Assert.NotNull(emitter.DurationMs);
        Assert.True(emitter.DurationMs >= 0);
    }

    [Fact]
    public async Task Middleware_marks_server_failures_without_persisting_exception_details()
    {
        var emitter = new FakeDiagnosticEmitter();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/models";

        var middleware = new DiagnosticRequestMiddleware(
            next: httpContext =>
            {
                httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Task.CompletedTask;
            },
            emitter);

        await middleware.InvokeAsync(context);

        Assert.Equal("warning", emitter.Level);
        Assert.Equal("http.status", emitter.EventCode);
        Assert.Equal("failed", emitter.Status);
        Assert.Equal("http_503", emitter.ErrorCode);
    }

    [Theory]
    [InlineData("/diagnostics/logs")]
    [InlineData("/diagnostics/status")]
    [InlineData("/diagnostics/export")]
    public async Task Middleware_does_not_emit_diagnostics_api_requests_to_avoid_self_refresh_feedback(string path)
    {
        var emitter = new FakeDiagnosticEmitter();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;

        var middleware = new DiagnosticRequestMiddleware(
            next: _ => Task.CompletedTask,
            emitter);

        await middleware.InvokeAsync(context);

        Assert.Null(emitter.EventCode);
        Assert.Null(emitter.DurationMs);
    }

    private sealed class FakeDiagnosticEmitter : IDiagnosticEmitter
    {
        public string? Level { get; private set; }
        public string? Component { get; private set; }
        public string? EventCode { get; private set; }
        public double? DurationMs { get; private set; }
        public string? Status { get; private set; }
        public string? ErrorCode { get; private set; }

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
            string? endpointHash = null,
            string? exceptionSite = null)
        {
            Level = level;
            Component = component;
            EventCode = eventCode;
            DurationMs = durationMs;
            Status = status;
            ErrorCode = errorCode;
            return true;
        }
    }
}
