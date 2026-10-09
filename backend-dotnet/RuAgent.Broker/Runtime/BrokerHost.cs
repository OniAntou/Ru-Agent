using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebSockets;
using OpenTelemetry;
using OpenTelemetry.Trace;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Diagnostics;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Transport;
using RuAgent.Broker.Tools;

namespace RuAgent.Broker.Runtime;

public sealed class BrokerHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly BrokerSession _session;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly BrokerCleanup _cleanup;
    private readonly ToolExecutionService _tools;
    private readonly TracerProvider? _telemetry;
    private readonly PendingApprovalStore _pendingV2 = new();
    private readonly ApprovalTokenService _authTokens = new();
    private readonly object _connectionSync = new();
    private readonly HashSet<Task> _connections = [];
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private bool _disposed;

    private BrokerHost(
        WebApplication application,
        BrokerSession session,
        BrokerOptions? options)
    {
        _application = application;
        _session = session;
        session.ConfigureAdvertisedCapabilities(AdvertisedCapabilities(options?.Web));
        _tools = new ToolExecutionService(
            session,
            ResolveWorkspaceRoot(),
            options?.Web,
            approvalTokens: _authTokens,
            trustedExecutableDirectories: options?.TrustedExecutableDirectories);
        _tools.CleanStaleSpoolFiles();
        _cleanup = new BrokerCleanup(
            stopListener: cancellationToken => new ValueTask(_application.StopAsync(cancellationToken)),
            cancelRequests: () => _shutdown.Cancel(),
            killChildren: Array.Empty<Action>(),
            session.DescriptorPath,
            session.Descriptor.SessionRoot);
        _telemetry = BrokerTelemetry.CreateProvider();
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
        BrokerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await StartCoreAsync(
            options.DescriptorPath,
            options,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BrokerHost> StartCoreAsync(
        string descriptorPath,
        BrokerOptions? options,
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

        var host = new BrokerHost(application, session, options);
        application.MapGet("/health", host.HandleHealthAsync);
        application.Map("/agent", host.HandleAgentAsync);
        await application.StartAsync(cancellationToken).ConfigureAwait(false);
        host.IsReady = true;
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        Task[] connections;
        lock (_connectionSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connections = _connections.ToArray();
        }

        IsReady = false;
        try { _shutdown.Cancel(); } catch { }

        try
        {
            await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _tools.ClearPendingApprovals();
        _pendingV2.Clear();
        _authTokens.Clear();
        await _cleanup.RunAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        try { _telemetry?.Dispose(); } catch { }
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
        var connectionTask = new AgentConnection(
            _session,
            profile => _tools.ListCapabilities(profile),
            _tools.ListCapabilities()
                .SelectMany(capability => capability.Scopes)
                .ToHashSet(StringComparer.Ordinal),
            (request, token) => ExecuteToolAsync(request, token),
            sessionId => _tools.RemoveSession(sessionId),
            pendingV2Store: _pendingV2,
            approvalTokenService: _authTokens,
            advertisedCapabilities: _session.AdvertisedCapabilities,
            lifecycleGate: _lifecycleGate,
            authenticatedToolHandler: (request, token) => ExecuteToolAsync(request, token, isAuthenticatedV2Execution: true),
            sessionTasksHandler: (request, token) => ExecuteSessionTasksAsync(request, token))
            .RunAsync(socket, linkedCancellation.Token);
        var tracked = false;
        lock (_connectionSync)
        {
            if (!_disposed)
            {
                _connections.Add(connectionTask);
                tracked = true;
            }
        }

        if (!tracked)
        {
            linkedCancellation.Cancel();
        }

        try
        {
            await connectionTask.ConfigureAwait(false);
        }
        finally
        {
            if (tracked)
            {
                lock (_connectionSync)
                {
                    _connections.Remove(connectionTask);
                }
            }
        }

    }

    private static IReadOnlyList<string> AdvertisedCapabilities(Configuration.WebOptions? web)
    {
        return RuAgent.Protocol.Harness.BrokerPolicyContracts.AuthoritativeFallbackCapabilities();
    }

    private async Task<RuAgent.Protocol.AgentEnvelope> ExecuteToolAsync(
        RuAgent.Protocol.AgentEnvelope request,
        CancellationToken cancellationToken,
        bool isAuthenticatedV2Execution = false)
    {
        using var telemetryActivity = BrokerTelemetry.StartToolActivity();
        var result = request.MessageType == RuAgent.Protocol.AgentMessageTypes.ApprovalResponse
            ? await _tools.RespondToApprovalAsync(request, cancellationToken).ConfigureAwait(false)
            : isAuthenticatedV2Execution
                ? await _tools.ExecuteAuthenticatedV2Async(request, cancellationToken).ConfigureAwait(false)
                : await _tools.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        BrokerTelemetry.SetToolOutcome(
            telemetryActivity,
            BrokerTelemetry.ReadToolId(request.Payload),
            result);
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

    private Task<RuAgent.Protocol.AgentEnvelope> ExecuteSessionTasksAsync(
        RuAgent.Protocol.AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        string? killExecutionId = null;
        if (request.Payload["kill_execution_id"] is System.Text.Json.Nodes.JsonValue killValue
            && killValue.TryGetValue<string>(out var killRaw)
            && !string.IsNullOrWhiteSpace(killRaw))
        {
            if (killRaw.Length > RuAgent.Protocol.TerminalLimits.MaxExecutionIdChars
                || killRaw.Contains('\0'))
            {
                return Task.FromResult(SessionTasksError(request, "invalid_arguments", "kill_execution_id is invalid."));
            }

            killExecutionId = killRaw;
        }

        try
        {
            var payload = _tools.HandleSessionTasks(request.SessionId, killExecutionId);
            payload["status"] = RuAgent.Protocol.ToolResultStatuses.Succeeded;
            payload["safe_summary"] = "session tasks";
            return Task.FromResult(new RuAgent.Protocol.AgentEnvelope(
                request.ProtocolVersion,
                request.RequestId,
                RuAgent.Protocol.AgentMessageTypes.SessionTasksResult,
                request.SessionId,
                request.CorrelationId,
                request.DeadlineMs,
                payload));
        }
        catch (ArgumentException error)
        {
            return Task.FromResult(SessionTasksError(request, error.Message, "Session task control failed."));
        }
    }

    private static RuAgent.Protocol.AgentEnvelope SessionTasksError(
        RuAgent.Protocol.AgentEnvelope request,
        string code,
        string summary) =>
        new(
            request.ProtocolVersion,
            request.RequestId,
            RuAgent.Protocol.AgentMessageTypes.SessionTasksResult,
            request.SessionId,
            request.CorrelationId,
            request.DeadlineMs,
            new System.Text.Json.Nodes.JsonObject
            {
                ["status"] = RuAgent.Protocol.ToolResultStatuses.Failed,
                ["error_code"] = code,
                ["safe_summary"] = summary,
            });

    private bool IsAuthorized(HttpContext context)
    {
        var candidate = context.Request.Headers.Authorization.ToString();
        var expected = "Bearer " + _session.Descriptor.AuthToken;
        if (candidate.Length == 0 || expected.Length == "Bearer ".Length)
        {
            return false;
        }

        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(candidateHash, expectedHash);
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.CompleteAsync().ConfigureAwait(false);
    }

    private static string ResolveWorkspaceRoot()
    {
        var configured = Environment.GetEnvironmentVariable("RU_WORKSPACE_ROOT")
            ?? Environment.GetEnvironmentVariable("RU_PROJECT_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (Directory.Exists(full))
            {
                return full;
            }
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
