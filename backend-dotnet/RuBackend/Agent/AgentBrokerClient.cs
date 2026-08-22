using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Protocol;

namespace RuBackend.Agent;

public sealed class AgentBrokerClient : IAgentBrokerClient
{
    private const int MaxFrameBytes = 64 * 1024;
    private readonly BrokerSessionDescriptor _descriptor;
    private readonly AgentSessionGrantStore _grants;
    private readonly AgentApprovalStore _approvals;
    private int _disposed;

    public AgentBrokerClient(
        string descriptorPath,
        AgentSessionGrantStore? grants = null,
        AgentApprovalStore? approvals = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorPath);
        _descriptor = LoadDescriptor(descriptorPath);
        _grants = grants ?? new AgentSessionGrantStore();
        _approvals = approvals ?? new AgentApprovalStore();
    }

    public async Task<AgentSessionGrant> OpenSessionAsync(
        string sessionId,
        IReadOnlySet<string> scopes,
        IReadOnlySet<string> skillIds,
        string? correlationId,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(skillIds);
        var response = await RoundTripAsync(
            new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                NewRequestId("session"),
                AgentMessageTypes.SessionOpen,
                sessionId,
                correlationId,
                deadlineMs,
                new JsonObject
                {
                    ["scopes"] = new JsonArray(scopes.Select(scope => (JsonNode?)scope).ToArray()),
                    ["skill_ids"] = new JsonArray(skillIds.Select(skill => (JsonNode?)skill).ToArray()),
                }),
            cancellationToken).ConfigureAwait(false);
        var result = ReadResult(response, sessionId);
        if (!string.Equals(result.Status, ToolResultStatuses.Succeeded, StringComparison.Ordinal))
        {
            throw new AgentBrokerException(result.ErrorCode ?? "session_open_failed");
        }

        var grant = new AgentSessionGrant(
            sessionId,
            new HashSet<string>(scopes, StringComparer.Ordinal),
            new HashSet<string>(skillIds, StringComparer.Ordinal),
            _descriptor.ExpiresAt);
        _grants.Put(grant);
        return grant;
    }

    public Task<AgentToolResult> RunSkillAsync(
        string sessionId,
        string skillId,
        JsonObject input,
        string? correlationId,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentNullException.ThrowIfNull(input);
        if (!_grants.TryGet(sessionId, "skill.execute", out var grant)
            || !grant!.SkillIds.Contains(skillId))
        {
            return Task.FromResult(Denied("skill_scope_missing", "Skill is not granted."));
        }

        return RunSkillCoreAsync(sessionId, skillId, input, correlationId, deadlineMs, cancellationToken);
    }

    public Task<AgentToolResult> ExecuteToolAsync(
        string sessionId,
        string toolId,
        string toolVersion,
        JsonObject arguments,
        string requiredScope,
        string? correlationId,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredScope);
        if (!_grants.TryGet(sessionId, requiredScope, out _))
        {
            return Task.FromResult(Denied("scope_missing", "Tool scope is not granted."));
        }

        return ExecuteToolCoreAsync(
            sessionId,
            toolId,
            toolVersion,
            arguments,
            correlationId,
            deadlineMs,
            cancellationToken);
    }

    public Task<AgentToolResult> RespondToApprovalAsync(
        AgentApprovalResponse response,
        string toolId,
        string toolVersion,
        JsonObject arguments,
        string? correlationId,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!_approvals.TryConsume(
                response.RequestId,
                response.SessionId,
                toolId,
                toolVersion,
                arguments,
                out _))
        {
            return Task.FromResult(Denied("approval_stale", "Approval is missing or expired."));
        }

        return RespondToApprovalCoreAsync(response, correlationId, deadlineMs, cancellationToken);
    }

    public async Task CancelAsync(
        string sessionId,
        string requestId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        _ = await RoundTripAsync(
            new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                requestId,
                AgentMessageTypes.Cancel,
                sessionId,
                correlationId,
                AgentEnvelopeCodec.MaxDeadlineMs,
                new JsonObject()),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseSessionAsync(
        string sessionId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        _ = await RoundTripAsync(
            new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                NewRequestId("close"),
                AgentMessageTypes.SessionClose,
                sessionId,
                correlationId,
                AgentEnvelopeCodec.MaxDeadlineMs,
                new JsonObject()),
            cancellationToken).ConfigureAwait(false);
        _grants.Remove(sessionId);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private async Task<AgentToolResult> RunSkillCoreAsync(
        string sessionId,
        string skillId,
        JsonObject input,
        string? correlationId,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        var response = await RoundTripAsync(
            CreateRequest(
                NewRequestId("skill"),
                AgentMessageTypes.SkillStart,
                sessionId,
                correlationId,
                deadlineMs,
                new JsonObject { ["skill_id"] = skillId, ["input"] = input.DeepClone() }),
            cancellationToken).ConfigureAwait(false);
        return ReadResult(response, sessionId);
    }

    private async Task<AgentToolResult> ExecuteToolCoreAsync(
        string sessionId,
        string toolId,
        string toolVersion,
        JsonObject arguments,
        string? correlationId,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        var response = await RoundTripAsync(
            CreateRequest(
                NewRequestId("tool"),
                AgentMessageTypes.ToolRequest,
                sessionId,
                correlationId,
                deadlineMs,
                new JsonObject
                {
                    ["tool_id"] = toolId,
                    ["tool_version"] = toolVersion,
                    ["arguments"] = arguments.DeepClone(),
                }),
            cancellationToken).ConfigureAwait(false);
        return ReadResult(response, sessionId);
    }

    private async Task<AgentToolResult> RespondToApprovalCoreAsync(
        AgentApprovalResponse approval,
        string? correlationId,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        var response = await RoundTripAsync(
            CreateRequest(
                approval.RequestId,
                AgentMessageTypes.ApprovalResponse,
                approval.SessionId,
                correlationId,
                deadlineMs,
                new JsonObject
                {
                    ["approved"] = approval.Approved,
                    ["arguments_hash"] = approval.ArgumentsHash,
                }),
            cancellationToken).ConfigureAwait(false);
        return ReadResult(response, approval.SessionId);
    }

    private async Task<AgentEnvelope> RoundTripAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateDeadline(request.DeadlineMs);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + _descriptor.AuthToken);
        var endpoint = new Uri(
            _descriptor.BaseUrl.Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
                .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).TrimEnd('/')
                + "/agent");
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

        var hello = request with
        {
            RequestId = NewRequestId("hello"),
            MessageType = AgentMessageTypes.Hello,
            Payload = new JsonObject { ["client"] = "RuBackend" },
        };
        await SendAsync(socket, hello, cancellationToken).ConfigureAwait(false);
        var helloReply = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        if (helloReply.RequestId != hello.RequestId
            || helloReply.MessageType != AgentMessageTypes.HelloOk
            || ReadInt(helloReply.Payload, "protocol_version") != AgentEnvelopeCodec.ProtocolVersion)
        {
            throw new AgentBrokerException("invalid_hello");
        }

        await SendAsync(socket, request, cancellationToken).ConfigureAwait(false);
        var reply = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(reply.RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(reply.SessionId, request.SessionId, StringComparison.Ordinal)
            || !string.Equals(reply.CorrelationId, request.CorrelationId, StringComparison.Ordinal))
        {
            throw new AgentBrokerException("stale_agent_result");
        }

        return reply;
    }

    private static AgentEnvelope CreateRequest(
        string requestId,
        string messageType,
        string sessionId,
        string? correlationId,
        int deadlineMs,
        JsonObject payload) =>
        new(
            AgentEnvelopeCodec.ProtocolVersion,
            requestId,
            messageType,
            sessionId,
            correlationId,
            deadlineMs,
            payload);

    private static AgentToolResult ReadResult(AgentEnvelope envelope, string sessionId)
    {
        var status = ReadString(envelope.Payload, "status", ToolResultStatuses.Failed);
        if (!ToolResultStatuses.Known.Contains(status))
        {
            throw new AgentBrokerException("invalid_tool_result");
        }

        var output = envelope.Payload["output"] as JsonObject ?? new JsonObject();
        var safeSummary = ReadString(envelope.Payload, "safe_summary", "Agent operation completed.");
        if (safeSummary.Length > 4096)
        {
            throw new AgentBrokerException("invalid_tool_result");
        }

        return new AgentToolResult(
            envelope.RequestId,
            status,
            output.DeepClone().AsObject(),
            ReadOptionalString(envelope.Payload, "error_code"),
            safeSummary);
    }

    private static AgentToolResult Denied(string code, string summary) =>
        new(string.Empty, ToolResultStatuses.Denied, new JsonObject(), code, summary);

    private static BrokerSessionDescriptor LoadDescriptor(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var descriptor = JsonSerializer.Deserialize<BrokerSessionDescriptor>(
            File.ReadAllText(fullPath),
            BrokerSessionDescriptor.JsonOptions)
            ?? throw new AgentBrokerException("invalid_descriptor");
        if (!Uri.TryCreate(descriptor.BaseUrl, UriKind.Absolute, out var uri)
            || !uri.IsLoopback
            || uri.Scheme is not ("http" or "https")
            || descriptor.ProtocolVersion != AgentEnvelopeCodec.ProtocolVersion
            || string.IsNullOrWhiteSpace(descriptor.AuthToken)
            || descriptor.ExpiresAt <= DateTimeOffset.UtcNow
            || !Path.IsPathFullyQualified(descriptor.SessionRoot))
        {
            throw new AgentBrokerException("invalid_descriptor");
        }

        return descriptor;
    }

    private static async Task SendAsync(
        ClientWebSocket socket,
        AgentEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(AgentEnvelopeCodec.Serialize(envelope));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<AgentEnvelope> ReceiveAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close
                || result.MessageType != WebSocketMessageType.Text)
            {
                throw new AgentBrokerException("broker_unavailable");
            }

            if (stream.Length + result.Count > MaxFrameBytes)
            {
                throw new AgentBrokerException("frame_too_large");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return AgentEnvelopeCodec.Parse(Encoding.UTF8.GetString(stream.ToArray()));
            }
        }
    }

    private static string NewRequestId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static string ReadString(JsonObject payload, string key, string fallback)
    {
        return payload[key] is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result)
            ? result
            : fallback;
    }

    private static string? ReadOptionalString(JsonObject payload, string key) =>
        payload[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static int ReadInt(JsonObject payload, string key) =>
        payload[key] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : throw new AgentBrokerException("invalid_hello");

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > AgentEnvelopeCodec.MaxIdLength)
        {
            throw new ArgumentException("Session ID is invalid.", nameof(sessionId));
        }
    }

    private static void ValidateDeadline(int deadlineMs)
    {
        if (deadlineMs is < 1 or > AgentEnvelopeCodec.MaxDeadlineMs)
        {
            throw new ArgumentOutOfRangeException(nameof(deadlineMs));
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

public sealed class AgentBrokerException : Exception
{
    public AgentBrokerException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}
