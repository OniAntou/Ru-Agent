using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebSockets;
using RuAgent.Broker.Transport;

namespace RuAgent.Broker.Runtime;

public sealed class BrokerHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly BrokerSession _session;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly BrokerCleanup _cleanup;
    private bool _disposed;

    private BrokerHost(WebApplication application, BrokerSession session)
    {
        _application = application;
        _session = session;
        _cleanup = new BrokerCleanup(
            stopListener: cancellationToken => new ValueTask(_application.StopAsync(cancellationToken)),
            cancelRequests: () => _shutdown.Cancel(),
            killChildren: Array.Empty<Action>(),
            session.DescriptorPath,
            session.Descriptor.SessionRoot);
    }

    public string BaseUrl => _session.Descriptor.BaseUrl.TrimEnd('/');
    public bool IsReady { get; private set; }
    public BrokerSession Session => _session;

    public static async Task<BrokerHost> StartAsync(
        string descriptorPath,
        CancellationToken cancellationToken = default)
    {
        var session = BrokerSession.Load(descriptorPath);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(BrokerHost).Assembly.GetName().Name,
            Args = [],
        });
        builder.WebHost.UseUrls(session.Descriptor.BaseUrl);
        builder.WebHost.SuppressStatusMessages(true);

        var application = builder.Build();
        application.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
        });

        var host = new BrokerHost(application, session);
        application.MapGet("/health", host.HandleHealthAsync);
        application.Map("/agent", host.HandleAgentAsync);
        await application.StartAsync(cancellationToken).ConfigureAwait(false);
        host.IsReady = true;
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsReady = false;
        await _cleanup.RunAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task HandleHealthAsync(HttpContext context)
    {
        if (!IsAuthorized(context))
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = IsReady ? "ready" : "starting",
            protocol_version = RuAgent.Protocol.AgentEnvelopeCodec.ProtocolVersion,
        }).ConfigureAwait(false);
    }

    private async Task HandleAgentAsync(HttpContext context)
    {
        if (!IsAuthorized(context))
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            _shutdown.Token);
        await new AgentConnection(_session).RunAsync(socket, linkedCancellation.Token).ConfigureAwait(false);
    }

    private bool IsAuthorized(HttpContext context)
    {
        var expected = "Bearer " + _session.Descriptor.AuthToken;
        return string.Equals(
            context.Request.Headers.Authorization.ToString(),
            expected,
            StringComparison.Ordinal);
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.CompleteAsync().ConfigureAwait(false);
    }
}
