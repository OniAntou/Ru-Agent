using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Protocol;

namespace RuAgent.Broker.Transport;

public sealed partial class AgentConnection
{
    public const int MaxFrameBytes = 64 * 1024;

    private readonly BrokerSession _session;
    private readonly Func<CapabilityProfile, IReadOnlyList<AgentToolCapability>> _capabilityProvider;
    private readonly IReadOnlySet<string> _executableScopes;
    private readonly Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? _toolHandler;
    private readonly Action<string>? _sessionClosedHandler;
    private readonly PendingApprovalStore _pendingV2;
    private readonly ApprovalTokenService _authTokens;
    private readonly IReadOnlyList<string>? _advertisedCapabilities;
    private readonly IWorkspaceCleanlinessProvider? _cleanliness;

    public AgentConnection(
        BrokerSession session,
        Func<CapabilityProfile, IReadOnlyList<AgentToolCapability>> capabilityProvider,
        IReadOnlySet<string> executableScopes,
        Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? toolHandler = null,
        Action<string>? sessionClosedHandler = null,
        PendingApprovalStore? pendingV2Store = null,
        ApprovalTokenService? approvalTokenService = null,
        IReadOnlyList<string>? advertisedCapabilities = null,
        IWorkspaceCleanlinessProvider? cleanliness = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _capabilityProvider = capabilityProvider ?? throw new ArgumentNullException(nameof(capabilityProvider));
        _executableScopes = new HashSet<string>(
            executableScopes ?? throw new ArgumentNullException(nameof(executableScopes)),
            StringComparer.Ordinal);
        _toolHandler = toolHandler;
        _sessionClosedHandler = sessionClosedHandler;
        // State is scoped to the BrokerHost by callers that need reconnect support;
        // a directly constructed connection gets private state instead of sharing
        // approvals/tokens with unrelated hosts or test instances.
        _pendingV2 = pendingV2Store ?? new PendingApprovalStore();
        _authTokens = approvalTokenService ?? new ApprovalTokenService();
        _advertisedCapabilities = advertisedCapabilities;
        _cleanliness = cleanliness;
    }

    internal PendingApprovalStore PendingV2Store => _pendingV2;

    internal ApprovalTokenService AuthTokens => _authTokens;

    public async Task RunAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                string? received;
                try
                {
                    received = await ReceiveTextAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (AgentProtocolException error)
                {
                    await SendAsync(
                        socket,
                        ProtocolErrorEnvelope(error.Code),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (received is null)
                {
                    break;
                }

                AgentEnvelope request;
                try
                {
                    request = AgentEnvelopeCodec.Parse(received);
                }
                catch (AgentProtocolException error)
                {
                    await SendAsync(socket, ProtocolErrorEnvelope(error.Code), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var reply = await HandleAsync(request, cancellationToken).ConfigureAwait(false);
                await SendAsync(socket, reply, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<AgentEnvelope> HandleAsync(AgentEnvelope request, CancellationToken cancellationToken)
    {
        if (request.ProtocolVersion == AgentEnvelopeCodec.ProtocolVersion && AgentMessageTypes.V2Only.Contains(request.MessageType))
        {
            return ToolError(request, "unsupported_message", "v2 message on v1 connection");
        }

        switch (request.MessageType)
        {
            case AgentMessageTypes.Hello:
                return HandleHello(request);
            case AgentMessageTypes.Health:
                return Reply(request, AgentMessageTypes.HealthOk, new JsonObject
            {
                ["status"] = "ready",
            });
            case AgentMessageTypes.SessionOpen:
                return OpenSession(request);
            case AgentMessageTypes.SessionClose:
                return CloseSession(request);
            case AgentMessageTypes.RunOpen:
                return HandleRunOpen(request);
            case AgentMessageTypes.RunPolicyRebind:
                return HandleRunPolicyRebind(request);
            case AgentMessageTypes.RunClose:
                return HandleRunClose(request);
            case AgentMessageTypes.ToolRequest:
            case AgentMessageTypes.ApprovalResponse:
                if (!_session.Owns(request.SessionId))
                {
                    return ToolError(request, "broker_not_ready", "session is not open");
                }

                return _toolHandler is null
                    ? ToolError(request, "broker_not_ready", "tool execution is not registered")
                    : await _toolHandler(request, cancellationToken).ConfigureAwait(false);
            case AgentMessageTypes.ToolEvaluate:
                return await HandleToolEvaluateAsync(request, cancellationToken).ConfigureAwait(false);
            case AgentMessageTypes.ToolExecute:
                return await HandleToolExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            case AgentMessageTypes.ToolOutputDelta:
                // Reserved for phase 3 terminal streaming; explicitly rejected, never ignored.
                return Reply(request, AgentMessageTypes.ToolExecuteResult, new JsonObject
                {
                    ["status"] = ToolResultStatuses.Denied,
                    ["error_code"] = "phase_not_available",
                    ["safe_summary"] = "tool output delta is reserved for phase 3",
                });
            case AgentMessageTypes.ApprovalRespond:
                if (!_session.Owns(request.SessionId))
                {
                    return ToolError(request, "broker_not_ready", "session is not open");
                }

                return await HandleApprovalRespondAsync(request, cancellationToken).ConfigureAwait(false);
            case AgentMessageTypes.SessionGrantsRevoke:
                return HandleSessionGrantsRevoke(request);
            case AgentMessageTypes.Cancel:
                return Reply(request, AgentMessageTypes.ToolResult, new JsonObject
            {
                ["status"] = ToolResultStatuses.Cancelled,
                ["safe_summary"] = "request cancelled",
            });
            default:
                return ToolError(request, "unsupported_message", "message type is not supported");
        }
    }

    private AgentEnvelope OpenSession(AgentEnvelope request)
    {
        var scopes = ReadScopes(request.Payload);
        if (scopes.Any(scope => !_executableScopes.Contains(scope)))
        {
            return ToolError(request, "scope_unavailable", "scope is not executable");
        }

        if (!_session.Open(request.SessionId, scopes))
        {
            return ToolError(request, "session_conflict", "session is already owned");
        }

        return Reply(request, AgentMessageTypes.SessionOpened, new JsonObject
        {
            ["status"] = ToolResultStatuses.Succeeded,
            ["scopes"] = new JsonArray(_session.Scopes
                .OrderBy(scope => scope, StringComparer.Ordinal)
                .Select(scope => (JsonNode?)scope).ToArray()),
            ["safe_summary"] = "session opened",
        });
    }

    private AgentEnvelope CloseSession(AgentEnvelope request)
    {
        var owned = _session.Owns(request.SessionId);
        _session.Close(request.SessionId);
        if (owned)
        {
            _sessionClosedHandler?.Invoke(request.SessionId);
        }
        return Reply(request, AgentMessageTypes.ToolResult, new JsonObject
        {
            ["status"] = "succeeded",
            ["safe_summary"] = "session closed",
        });
    }

    private AgentEnvelope HandleHello(AgentEnvelope request)
    {
        CapabilityProfile profile;
        try
        {
            profile = ReadProfile(request.Payload);
        }
        catch (AgentProtocolException error)
        {
            return new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                request.RequestId,
                AgentMessageTypes.ToolResult,
                request.SessionId,
                request.CorrelationId,
                request.DeadlineMs,
                new JsonObject
                {
                    ["status"] = ToolResultStatuses.Failed,
                    ["error_code"] = error.Code,
                    ["safe_summary"] = "invalid capability profile",
                });
        }

        var advertisedVersion = request.ProtocolVersion == AgentEnvelopeCodec.ProtocolVersionV2
            ? AgentEnvelopeCodec.ProtocolVersionV2
            : AgentEnvelopeCodec.ProtocolVersion;
        var capabilities = _capabilityProvider(profile);
        // A capability is model-visible only when the active session actually
        // owns every required scope. Before SessionOpen, keep optional web
        // bindings hidden so a configured endpoint alone never advertises
        // network access to an unauthenticated/ungranted session.
        if (_session.Owns(request.SessionId))
        {
            capabilities = capabilities
                .Where(capability => capability.Scopes.All(_session.Scopes.Contains))
                .ToArray();
        }
        else
        {
            capabilities = capabilities
                .Where(capability => !capability.Scopes.Contains(ToolScope.WebSearch)
                    && !capability.Scopes.Contains(ToolScope.WebFetch))
                .ToArray();
        }
        if (request.ProtocolVersion == AgentEnvelopeCodec.ProtocolVersionV2 && _session.ActiveRunLease is { } lease)
        {
            // Filter capabilities to lease-advertised ones in v2.
            capabilities = capabilities.Where(c => lease.AdvertisedCapabilities.Contains(c.ToolId)).ToArray();
        }
        else if (request.ProtocolVersion == AgentEnvelopeCodec.ProtocolVersionV2)
        {
            // Before run lease, only read-only fallback is advertised per phase-1.
            var fallback = new HashSet<string>(RuAgent.Protocol.Harness.BrokerPolicyContracts.AuthoritativeFallbackCapabilities(), StringComparer.Ordinal);
            capabilities = capabilities.Where(c => fallback.Contains(c.ToolId)).ToArray();
        }

        return Reply(request, AgentMessageTypes.HelloOk, new JsonObject
        {
            ["status"] = "ready",
            ["protocol_version"] = advertisedVersion,
            ["tools"] = new JsonArray(capabilities
                .Select(capability => (JsonNode?)ToJson(capability)).ToArray()),
        });
    }

    private static CapabilityProfile ReadProfile(JsonObject payload)
    {
        var value = payload["profile"];
        if (value is null)
        {
            return CapabilityProfile.Chat;
        }

        if (value is not JsonValue item || !item.TryGetValue<string>(out var profile))
        {
            throw new AgentProtocolException("invalid_profile");
        }

        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 32)
        {
            throw new AgentProtocolException("invalid_profile");
        }

        return profile switch
        {
            "chat" => CapabilityProfile.Chat,
            "agent" => CapabilityProfile.Agent,
            // Legacy "full" maps to the single Agent policy: no second tier.
            "full" => CapabilityProfile.Agent,
            _ => throw new AgentProtocolException("invalid_profile"),
        };
    }

    private static JsonObject ToJson(AgentToolCapability capability) => new()
    {
        ["tool_id"] = capability.ToolId,
        ["tool_version"] = capability.ToolVersion,
        ["scopes"] = new JsonArray(capability.Scopes
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .Select(scope => (JsonNode?)scope).ToArray()),
        ["risk"] = capability.Risk,
        ["availability"] = capability.Availability,
        ["model_enabled"] = capability.ModelEnabled,
    };

    private static AgentEnvelope Reply(AgentEnvelope request, string type, JsonObject payload)
        => new(
            request.ProtocolVersion,
            request.RequestId,
            type,
            request.SessionId,
            request.CorrelationId,
            Math.Min(request.DeadlineMs, AgentEnvelopeCodec.MaxDeadlineMs),
            payload);

    private static AgentEnvelope ProtocolErrorEnvelope(string errorCode) =>
        new(
            AgentEnvelopeCodec.ProtocolVersion,
            "protocol-error",
            AgentMessageTypes.ToolResult,
            "unknown",
            null,
            1_000,
            new JsonObject
            {
                ["status"] = ToolResultStatuses.Failed,
                ["error_code"] = errorCode,
                ["safe_summary"] = "invalid agent envelope",
            });

    private static async Task SendAsync(
        WebSocket socket,
        AgentEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(AgentEnvelopeCodec.Serialize(envelope));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string?> ReceiveTextAsync(
        WebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new AgentProtocolException("invalid_frame");
            }

            if (content.Length + result.Count > MaxFrameBytes)
            {
                throw new AgentProtocolException("frame_too_large");
            }

            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                try
                {
                    return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                        .GetString(content.ToArray());
                }
                catch (DecoderFallbackException error)
                {
                    throw new AgentProtocolException("invalid_utf8", error.Message);
                }
            }
        }
    }
}
