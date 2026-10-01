using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ApiRefreshCancellationTests
{
    [Fact]
    public async Task GetSessionsAsync_honors_cancellation_token()
    {
        using var api = new ApiService("test-secret", 59999);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.GetSessionsAsync(cts.Token));
    }

    [Fact]
    public async Task GetDiagnosticLogsAsync_honors_cancellation_token()
    {
        using var api = new ApiService("test-secret", 59999);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.GetDiagnosticLogsAsync(cancellationToken: cts.Token));
    }

    [Fact]
    public async Task GetDiagnosticStatusAsync_honors_cancellation_token()
    {
        using var api = new ApiService("test-secret", 59999);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.GetDiagnosticStatusAsync(cts.Token));
    }

    [Fact]
    public async Task Backend_health_requests_honor_cancellation_token()
    {
        using var handler = new BlockingHandler();
        var session = new BackendSession(new Uri("http://127.0.0.1:59999/"), "test-secret");
        using var api = new ApiService(session, handler);
        using var cts = new CancellationTokenSource();

        Task request = Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.CheckHealthDetailedAsync(cts.Token));

        cts.Cancel();
        await request;
        Assert.True(handler.CancellationObserved);
    }

    [Fact]
    public async Task ApiService_records_LastError_and_fires_RequestFailed_on_http_failure()
    {
        var logRoot = Path.Combine(
            Path.GetTempPath(),
            "ru-api-error-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logRoot);
        try
        {
            using var handler = new FailingHandler(HttpStatusCode.InternalServerError);
            var session = new BackendSession(new Uri("http://127.0.0.1:59999/"), "test-secret");
            using var api = new ApiService(session, handler, logRoot);

            ApiErrorEventArgs? capturedEvent = null;
            api.RequestFailed += (_, args) => capturedEvent = args;

            var result = await api.GetSettingsAsync();

            Assert.Null(result);
            Assert.NotNull(api.LastError);
            Assert.Equal("GetSettingsAsync", api.LastError.Method);
            Assert.Equal(500, api.LastError.StatusCode);
            Assert.NotNull(capturedEvent);
            Assert.Same(api.LastError, capturedEvent.Error);

            // The failure diagnostic must land in the injected root: a test that
            // exercises this path must never write into the operator's real log.
            var diagnosticFile = Assert.Single(Directory.GetFiles(logRoot, "gui-*.jsonl"));
            Assert.Contains("api_getsettingsasync_500", File.ReadAllText(diagnosticFile));
        }
        finally
        {
            try
            {
                Directory.Delete(logRoot, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        public FailingHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }
}
