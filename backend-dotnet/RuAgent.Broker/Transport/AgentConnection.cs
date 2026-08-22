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

    public AgentConnection(BrokerSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
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

                var reply = Handle(request);
                await SendAsync(socket, reply, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private AgentEnvelope Handle(AgentEnvelope request)
    {
        return request.MessageType switch
        {
            AgentMessageTypes.Hello => Reply(request, AgentMessageTypes.HelloOk, new JsonObject
            {
                ["status"] = "ready",
                ["protocol_version"] = AgentEnvelopeCodec.ProtocolVersion,
            }),
            AgentMessageTypes.Health => Reply(request, AgentMessageTypes.HealthOk, new JsonObject
            {
                ["status"] = "ready",
            }),
            AgentMessageTypes.SessionOpen => _session.Open(request.SessionId)
                ? Reply(request, AgentMessageTypes.SkillResult, new JsonObject
                {
                    ["status"] = "succeeded",
                    ["safe_summary"] = "session opened",
                })
                : ToolError(request, "session_conflict", "session is already owned"),
            AgentMessageTypes.SessionClose => CloseSession(request),
            AgentMessageTypes.ToolRequest or AgentMessageTypes.SkillStart =>
                _session.Owns(request.SessionId)
                    ? ToolError(request, "not_implemented", "tool execution is not registered")
                    : ToolError(request, "broker_not_ready", "session is not open"),
            AgentMessageTypes.Cancel => Reply(request, AgentMessageTypes.ToolResult, new JsonObject
            {
                ["status"] = ToolResultStatuses.Cancelled,
                ["safe_summary"] = "request cancelled",
            }),
            _ => ToolError(request, "unsupported_message", "message type is not supported"),
        };
    }

    private AgentEnvelope CloseSession(AgentEnvelope request)
    {
        _session.Close(request.SessionId);
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
