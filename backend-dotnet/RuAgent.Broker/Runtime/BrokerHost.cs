using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebSockets;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Transport;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Desktop;

namespace RuAgent.Broker.Runtime;

public sealed class BrokerHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly BrokerSession _session;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly BrokerCleanup _cleanup;
    private readonly ToolExecutionService _tools;
    private readonly PendingApprovalStore _pendingV2 = new();
    private readonly ApprovalTokenService _authTokens = new();
    private bool _disposed;

    private BrokerHost(
        WebApplication application,
        BrokerSession session,
        IVolumeController? volumeController)
    {
        _application = application;
        _session = session;
        _tools = new ToolExecutionService(session, ResolveWorkspaceRoot(), volumeController);
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
        return await StartCoreAsync(descriptorPath, null, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<BrokerHost> StartAsync(
        string descriptorPath,
        IVolumeController volumeController,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volumeController);
        return await StartCoreAsync(descriptorPath, volumeController, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BrokerHost> StartCoreAsync(
        string descriptorPath,
        IVolumeController? volumeController,
        CancellationToken cancellationToken)
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

        var host = new BrokerHost(application, session, volumeController);
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
        _session.ClearDecisions();
        _tools.ClearPendingApprovals();
        _pendingV2.Clear();
        _authTokens.Clear();
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
            protocol_version = _session.Descriptor.ProtocolVersion,
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
        await new AgentConnection(
            _session,
            profile => _tools.ListCapabilities(profile),
            _tools.ListCapabilities()
                .SelectMany(capability => capability.Scopes)
                .ToHashSet(StringComparer.Ordinal),
            (request, token) => ExecuteToolAsync(request, token),
            (request, token) => ExecuteApprovalAsync(request, token),
            sessionId => _tools.RemoveSession(sessionId),
            pendingV2Store: _pendingV2,
            approvalTokenService: _authTokens)
            .RunAsync(socket, linkedCancellation.Token).ConfigureAwait(false);
    }

    private async Task<RuAgent.Protocol.AgentEnvelope> ExecuteToolAsync(
        RuAgent.Protocol.AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        var result = await _tools.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return new RuAgent.Protocol.AgentEnvelope(
            request.ProtocolVersion,
            request.RequestId,
            RuAgent.Protocol.AgentMessageTypes.ToolResult,
            request.SessionId,
            request.CorrelationId,
            request.DeadlineMs,
            new System.Text.Json.Nodes.JsonObject
            {
                ["status"] = result.Status,
                ["output"] = result.Output.DeepClone(),
                ["error_code"] = result.ErrorCode,
                ["safe_summary"] = result.SafeSummary,
                ["duration_ms"] = result.DurationMs,
                ["verification_state"] = result.VerificationState,
                ["endpoint_role"] = result.EndpointRole,
                ["endpoint_hash"] = result.EndpointHash,
                ["adapter_invoked"] = result.AdapterInvoked,
            });
    }

    private async Task<RuAgent.Protocol.AgentEnvelope> ExecuteApprovalAsync(
        RuAgent.Protocol.AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        var result = await _tools.RespondToApprovalAsync(request, cancellationToken).ConfigureAwait(false);
        return new RuAgent.Protocol.AgentEnvelope(
            request.ProtocolVersion,
            request.RequestId,
            RuAgent.Protocol.AgentMessageTypes.ToolResult,
            request.SessionId,
            request.CorrelationId,
            request.DeadlineMs,
            new System.Text.Json.Nodes.JsonObject
            {
                ["status"] = result.Status,
                ["output"] = result.Output.DeepClone(),
                ["error_code"] = result.ErrorCode,
                ["safe_summary"] = result.SafeSummary,
                ["duration_ms"] = result.DurationMs,
                ["verification_state"] = result.VerificationState,
                ["endpoint_role"] = result.EndpointRole,
                ["endpoint_hash"] = result.EndpointHash,
                ["adapter_invoked"] = result.AdapterInvoked,
            });
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

    private static string ResolveWorkspaceRoot()
    {
        var configured = Environment.GetEnvironmentVariable("RU_WORKSPACE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src"))
                && Directory.Exists(Path.Combine(current.FullName, "backend-dotnet")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Environment.CurrentDirectory;
    }
}
