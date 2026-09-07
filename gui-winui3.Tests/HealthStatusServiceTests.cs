using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class HealthStatusServiceTests
{
    [Fact]
    public async Task GatherAsync_WhenBackendNotConfigured_ReturnsBackendDownAndWorkerOff()
    {
        using var handler = new MockHttpHandler();
        var session = new BackendSession(new Uri("http://127.0.0.1:59999/"), "test-secret");
        using var api = new ApiService(session, handler);
        var service = new HealthStatusService(api, () => false, isHarness: false);

        var snapshot = await service.GatherAsync(CancellationToken.None);

        Assert.False(snapshot.BackendRunning);
        Assert.False(snapshot.WorkerOk);
        Assert.Null(snapshot.WorkerError);
        Assert.False(snapshot.VtsConnected);
        Assert.False(snapshot.VtsSkipped);
    }

    [Fact]
    public async Task GatherAsync_WhenBackendConnectionFails_ReturnsBackendDown()
    {
        using var handler = new MockHttpHandler(onRequest: _ => throw new HttpRequestException("Connection refused"));
        var session = new BackendSession(new Uri("http://127.0.0.1:59999/"), "test-secret");
        using var api = new ApiService(session, handler);
        var service = new HealthStatusService(api, () => true, isHarness: false);

        var snapshot = await service.GatherAsync(CancellationToken.None);

        Assert.False(snapshot.BackendRunning);
        Assert.False(snapshot.WorkerOk);
        Assert.Null(snapshot.WorkerError);
        Assert.False(snapshot.VtsConnected);
    }

    [Fact]
    public async Task GatherAsync_WhenBackendRunning_AndWorkerWaiting_ReturnsBackendOkAndWorkerWaiting()
    {
        using var handler = new MockHttpHandler(onRequest: req =>
        {
            if (req.RequestUri?.AbsolutePath == "/status")
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var session = new BackendSession(new Uri("http://127.0.0.1:59999/"), "test-secret");
        using var api = new ApiService(session, handler);
        var service = new HealthStatusService(api, () => true, isHarness: false);

        var snapshot = await service.GatherAsync(CancellationToken.None);

        Assert.True(snapshot.BackendRunning);
        Assert.False(snapshot.WorkerOk);
        Assert.Null(snapshot.WorkerError);
    }

    [Fact]
    public async Task GatherAsync_WhenBackendRunning_AndWorkerError_ReturnsWorkerErrorMessage()
    {
        using var handler = new MockHttpHandler(onRequest: req =>
        {
            if (req.RequestUri?.AbsolutePath == "/status")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"error\",\"message\":\"Worker process crashed\"}", Encoding.UTF8, "application/json")
                };
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var session = new BackendSession(new Uri("http://127.0.0.1:59999/"), "test-secret");
        using var api = new ApiService(session, handler);
        var service = new HealthStatusService(api, () => true, isHarness: false);

        var snapshot = await service.GatherAsync(CancellationToken.None);

        Assert.True(snapshot.BackendRunning);
        Assert.False(snapshot.WorkerOk);
        Assert.Equal("Worker process crashed", snapshot.WorkerError);
    }

    [Fact]
    public async Task GatherAsync_WhenBackendRunning_AndWorkerHealthy_ReturnsReady()
    {
        using var handler = new MockHttpHandler(onRequest: req =>
        {
            if (req.RequestUri?.AbsolutePath == "/status")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"ready\"}", Encoding.UTF8, "application/json")
                };
            }
            if (req.RequestUri?.AbsolutePath == "/vts/status")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"connected\":true}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var session = new BackendSession(new Uri("http://127.0.0.1:59999/"), "test-secret");
        using var api = new ApiService(session, handler);
        var service = new HealthStatusService(api, () => true, isHarness: false);

        var snapshot = await service.GatherAsync(CancellationToken.None);

        Assert.True(snapshot.BackendRunning);
        Assert.True(snapshot.WorkerOk);
        Assert.Null(snapshot.WorkerError);
        Assert.True(snapshot.VtsConnected);
    }

    [Fact]
    public async Task GatherAsync_InHarnessMode_SkipsVts()
    {
        using var handler = new MockHttpHandler(onRequest: req =>
        {
            if (req.RequestUri?.AbsolutePath == "/status")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"ready\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var session = new BackendSession(new Uri("http://127.0.0.1:59999/"), "test-secret");
        using var api = new ApiService(session, handler);
        var service = new HealthStatusService(api, () => true, isHarness: true);

        var snapshot = await service.GatherAsync(CancellationToken.None);

        Assert.True(snapshot.BackendRunning);
        Assert.True(snapshot.WorkerOk);
        Assert.True(snapshot.VtsSkipped);
        Assert.False(snapshot.VtsConnected);
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _onRequest;

        public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage>? onRequest = null)
        {
            _onRequest = onRequest;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_onRequest != null)
            {
                return Task.FromResult(_onRequest(request));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
