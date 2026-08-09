using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace RuBackend.Tests.Fakes;

public sealed class WebSocketRelayFixture : IDisposable
{
    private readonly WebApplication _upstreamApp;
    private readonly BackendWebApplicationFactory _backendFactory;
    private readonly TestWebSocketPeer _upstream;
    private readonly StubUpstreamWebSocketConnector _connector;

    private WebSocketRelayFixture(
        WebApplication upstreamApp,
        BackendWebApplicationFactory backendFactory,
        TestWebSocketPeer upstream,
        StubUpstreamWebSocketConnector connector)
    {
        _upstreamApp = upstreamApp;
        _backendFactory = backendFactory;
        _upstream = upstream;
        _connector = connector;
    }

    public TestWebSocketPeer Upstream => _upstream;

    public Uri? UpstreamRequestUri => _connector.LastRequestedUri;

    public static async Task<WebSocketRelayFixture> StartAsync()
    {
        var accepted = new TaskCompletionSource<WebSocket>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamBuilder = WebApplication.CreateBuilder();
        upstreamBuilder.WebHost.UseTestServer();
        var upstreamApp = upstreamBuilder.Build();
        upstreamApp.UseWebSockets();
        upstreamApp.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var socket = await context.WebSockets.AcceptWebSocketAsync();
            accepted.TrySetResult(socket);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
        });
        await upstreamApp.StartAsync();
        var upstreamServer = upstreamApp.GetTestServer();

        var upstreamClient = await upstreamServer
            .CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/upstream"), CancellationToken.None);
        var upstreamServerSocket = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var connector = new StubUpstreamWebSocketConnector(upstreamServerSocket);
        var backendFactory = new BackendWebApplicationFactory(
            webSocketConnector: connector);
        var upstreamPeer = new TestWebSocketPeer(upstreamClient);

        return new WebSocketRelayFixture(upstreamApp, backendFactory, upstreamPeer, connector);
    }

    public HttpClient CreateHttpClient()
    {
        return _backendFactory.CreateClient();
    }

    public async Task<TestWebSocketPeer> ConnectClientAsync(string pathAndQuery)
    {
        var socket = await _backendFactory.Server
            .CreateWebSocketClient()
            .ConnectAsync(
                new Uri("ws://localhost" + pathAndQuery),
                CancellationToken.None);
        return new TestWebSocketPeer(socket);
    }

    public void Dispose()
    {
        _upstream.Dispose();
        _backendFactory.Dispose();
        _upstreamApp.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
