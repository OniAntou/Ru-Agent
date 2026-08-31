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

    [Theory]
    [InlineData("worker")]
    [InlineData("vts")]
    [InlineData("voice")]
    public async Task Health_requests_honor_cancellation_token(string endpoint)
    {
        using var handler = new BlockingHandler();
        var session = new BackendSession(new Uri("http://127.0.0.1:59999/"), "test-secret");
        using var api = new ApiService(session, handler);
        using var cts = new CancellationTokenSource();

        Task request = endpoint switch
        {
            "worker" => Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.CheckWorkerHealthAsync(cts.Token)),
            "voice" => Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetVoiceStatusAsync(cts.Token)),
            _ => Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetVtsStatusAsync(cts.Token)),
        };

        cts.Cancel();
        await request;
        Assert.True(handler.CancellationObserved);
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
