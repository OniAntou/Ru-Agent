using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Protocol;

namespace RuAgent.Broker.Transport;

public sealed class AgentConnection
{
    public const int MaxFrameBytes = 64 * 1024;

    private readonly BrokerSession _session;
    private readonly Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? _toolHandler;
    private readonly Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? _skillHandler;
    private readonly Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? _approvalHandler;
    private readonly Action<string>? _sessionClosedHandler;

    public AgentConnection(
        BrokerSession session,
        Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? toolHandler = null,
        Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? skillHandler = null,
        Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? approvalHandler = null,
        Action<string>? sessionClosedHandler = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _toolHandler = toolHandler;
        _skillHandler = skillHandler;
        _approvalHandler = approvalHandler;
        _sessionClosedHandler = sessionClosedHandler;
    }

    public async Task RunAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var received = await ReceiveTextAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
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
                    await SendAsync(
                        socket,
                        new AgentEnvelope(
                            AgentEnvelopeCodec.ProtocolVersion,
                            "protocol-error",
                            AgentMessageTypes.ToolResult,
                            "unknown",
                            null,
                            1_000,
                            new JsonObject
                            {
                                ["status"] = ToolResultStatuses.Failed,
                                ["error_code"] = error.Code,
                                ["safe_summary"] = "invalid agent envelope",
                            }),
                        cancellationToken).ConfigureAwait(false);
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
        switch (request.MessageType)
        {
            case AgentMessageTypes.Hello:
                return Reply(request, AgentMessageTypes.HelloOk, new JsonObject
            {
                ["status"] = "ready",
                ["protocol_version"] = AgentEnvelopeCodec.ProtocolVersion,
            });
            case AgentMessageTypes.Health:
                return Reply(request, AgentMessageTypes.HealthOk, new JsonObject
            {
                ["status"] = "ready",
            });
            case AgentMessageTypes.SessionOpen:
                return _session.Open(
                        request.SessionId,
                        ReadScopes(request.Payload),
                        ReadSkillIds(request.Payload))
                    ? Reply(request, AgentMessageTypes.SkillResult, new JsonObject
                {
                    ["status"] = "succeeded",
                    ["safe_summary"] = "session opened",
                })
                    : ToolError(request, "session_conflict", "session is already owned");
            case AgentMessageTypes.SessionClose:
                return CloseSession(request);
            case AgentMessageTypes.ToolRequest:
                if (!_session.Owns(request.SessionId))
                {
                    return ToolError(request, "broker_not_ready", "session is not open");
                }

                return _toolHandler is null
                    ? ToolError(request, "broker_not_ready", "tool execution is not registered")
                    : await _toolHandler(request, cancellationToken).ConfigureAwait(false);
            case AgentMessageTypes.SkillStart:
                if (!_session.Owns(request.SessionId))
                {
                    return ToolError(request, "broker_not_ready", "session is not open");
                }

                return _skillHandler is null
                    ? ToolError(request, "skill_not_ready", "skill execution is not registered")
                    : await _skillHandler(request, cancellationToken).ConfigureAwait(false);
            case AgentMessageTypes.ApprovalResponse:
                if (!_session.Owns(request.SessionId))
                {
                    return ToolError(request, "broker_not_ready", "session is not open");
                }

                return _approvalHandler is null
                    ? ToolError(request, "broker_not_ready", "approval handling is not registered")
                    : await _approvalHandler(request, cancellationToken).ConfigureAwait(false);
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

    private AgentEnvelope CloseSession(AgentEnvelope request)
    {
        _session.Close(request.SessionId);
        _sessionClosedHandler?.Invoke(request.SessionId);
        return Reply(request, AgentMessageTypes.SkillResult, new JsonObject
        {
            ["status"] = "succeeded",
            ["safe_summary"] = "session closed",
        });
    }

    private static AgentEnvelope ToolError(AgentEnvelope request, string errorCode, string summary)
        => Reply(request, AgentMessageTypes.ToolResult, new JsonObject
        {
            ["status"] = ToolResultStatuses.Denied,
            ["error_code"] = errorCode,
            ["safe_summary"] = summary,
        });

    private static IReadOnlySet<string> ReadScopes(JsonObject payload)
    {
        if (payload["scopes"] is not JsonArray values)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is JsonValue item && item.TryGetValue<string>(out var scope) && !string.IsNullOrWhiteSpace(scope))
            {
                result.Add(scope);
            }
        }

        return result;
    }

    private static IReadOnlySet<string> ReadSkillIds(JsonObject payload)
    {
        if (payload["skill_ids"] is not JsonArray values)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is JsonValue item
                && item.TryGetValue<string>(out var skillId)
                && !string.IsNullOrWhiteSpace(skillId)
                && skillId.Length <= 128)
            {
                result.Add(skillId);
            }
        }

        return result;
    }

    private static AgentEnvelope Reply(AgentEnvelope request, string type, JsonObject payload)
        => new(
            AgentEnvelopeCodec.ProtocolVersion,
            request.RequestId,
            type,
            request.SessionId,
            request.CorrelationId,
            Math.Min(request.DeadlineMs, AgentEnvelopeCodec.MaxDeadlineMs),
            payload);

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
                return Encoding.UTF8.GetString(content.ToArray());
            }
        }
    }
}
