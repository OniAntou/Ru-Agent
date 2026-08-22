using System.Collections.Frozen;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Protocol;

namespace RuBackend.Agent;

public sealed class AgentBrokerClient : IAgentBrokerClient
{
    private const int MaxFrameBytes = 64 * 1024;
    private static readonly Regex ToolIdPattern = new("^[a-z][a-z0-9]*(?:\\.[a-z0-9][a-z0-9_-]*)+$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.Compiled);
    private static readonly Regex ScopePattern = new("^[a-z][a-z0-9_.-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex EndpointHashPattern = new("^[0-9a-f]{16}$", RegexOptions.Compiled);
    private static readonly IReadOnlySet<string> Risks = new HashSet<string>(
        ["read", "reversible", "external", "destructive"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Availabilities = new HashSet<string>(
        ["executable", "native_pending"],
        StringComparer.Ordinal);

    private readonly BrokerSessionDescriptor _descriptor;
    private readonly AgentSessionGrantStore _grants;
    private readonly object _capabilityGate = new();
    private IReadOnlyList<AgentToolCapability>? _capabilities;
    private IReadOnlyDictionary<(string ToolId, string ToolVersion), AgentToolCapability>? _capabilityIndex;
    private int _disposed;

    public AgentBrokerClient(
        string descriptorPath,
        AgentSessionGrantStore? grants = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorPath);
        _descriptor = LoadDescriptor(descriptorPath);
        _grants = grants ?? new AgentSessionGrantStore();
    }

    public async Task<IReadOnlyList<AgentToolCapability>> GetCapabilitiesAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var cached = CapabilitySnapshot();
        if (cached is not null)
        {
            return cached;
        }

        _ = await RoundTripAsync(
            new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                NewRequestId("capabilities"),
                AgentMessageTypes.Health,
                "capabilities",
                null,
                5_000,
                new JsonObject()),
            cancellationToken).ConfigureAwait(false);
        return CapabilitySnapshot()
            ?? throw new AgentBrokerException("invalid_capabilities");
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
                    ["scopes"] = new JsonArray(scopes
                        .Order(StringComparer.Ordinal)
                        .Select(scope => (JsonNode?)scope).ToArray()),
                    ["skill_ids"] = new JsonArray(skillIds
                        .Order(StringComparer.Ordinal)
                        .Select(skill => (JsonNode?)skill).ToArray()),
                }),
            cancellationToken).ConfigureAwait(false);
        if (response.MessageType != AgentMessageTypes.SessionOpened)
        {
            var errorCode = ReadOptionalString(response.Payload, "error_code", "invalid_session_result");
            throw new AgentBrokerException(errorCode ?? "session_open_failed");
        }

        var status = ReadRequiredString(response.Payload, "status", "invalid_session_result");
        if (status != ToolResultStatuses.Succeeded)
        {
            throw new AgentBrokerException(
                ReadOptionalString(response.Payload, "error_code", "invalid_session_result")
                ?? "session_open_failed");
        }

        var actualScopes = ReadBoundedStringSet(response.Payload["scopes"], 16, 64, "invalid_session_result");
        var actualSkillIds = ReadBoundedStringSet(response.Payload["skill_ids"], 16, 128, "invalid_session_result");
        var grant = new AgentSessionGrant(
            sessionId,
            actualScopes,
            actualSkillIds,
            _descriptor.ExpiresAt);
        _grants.Put(grant);
        return grant;
    }

    public async Task<AgentToolResult> ExecuteToolAsync(
        AgentToolCall call,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(call);
        ValidateSessionId(call.SessionId);
        ValidateCall(call);
        _ = await GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);

        AgentToolCapability? capability;
        lock (_capabilityGate)
        {
            _capabilityIndex!.TryGetValue((call.ToolId, call.ToolVersion), out capability);
        }

        if (capability is null)
        {
            return Failed(call.RequestId, "capability_unavailable", "Tool capability is unavailable.");
        }

        if (!_grants.TryGet(call.SessionId, capability.Scopes, out _))
        {
            return Failed(call.RequestId, "scope_not_granted", "Tool scope is not granted.");
        }

        var arguments = JsonSerializer.SerializeToNode(call.Arguments) as JsonObject
            ?? throw new ArgumentException("Tool arguments are invalid.", nameof(call));
        var response = await RoundTripAsync(
            CreateRequest(
                call.RequestId,
                AgentMessageTypes.ToolRequest,
                call.SessionId,
                call.CorrelationId,
                call.DeadlineMs,
                new JsonObject
                {
                    ["tool_id"] = call.ToolId,
                    ["tool_version"] = call.ToolVersion,
                    ["capability_class"] = call.CapabilityClass,
                    ["arguments"] = arguments,
                }),
            cancellationToken).ConfigureAwait(false);
        return ReadResult(response);
    }

    public async Task<AgentToolResult> RespondToApprovalAsync(
        AgentApprovalResponse response,
        string? correlationId,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ValidateSessionId(response.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(response.ApprovalId);
        var envelope = await RoundTripAsync(
            CreateRequest(
                response.ApprovalId,
                AgentMessageTypes.ApprovalResponse,
                response.SessionId,
                correlationId,
                deadlineMs,
                new JsonObject
                {
                    ["approval_id"] = response.ApprovalId,
                    ["approved"] = response.Approved,
                }),
            cancellationToken).ConfigureAwait(false);
        return ReadResult(envelope);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_capabilityGate)
        {
            _capabilities = Array.AsReadOnly(Array.Empty<AgentToolCapability>());
            _capabilityIndex = new Dictionary<(string, string), AgentToolCapability>();
        }

        _grants.Clear();
        return ValueTask.CompletedTask;
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
            || ReadInt(helloReply.Payload, "protocol_version", "invalid_hello") != AgentEnvelopeCodec.ProtocolVersion)
        {
            throw new AgentBrokerException("invalid_hello");
        }

        CacheCapabilities(ParseCapabilities(helloReply.Payload));
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

    private static IReadOnlyList<AgentToolCapability> ParseCapabilities(JsonObject payload)
    {
        if (payload["tools"] is not JsonArray tools
            || tools.Count > 64
            || payload["skills"] is not JsonArray skills
            || skills.Count != 0)
        {
            throw new AgentBrokerException("invalid_capabilities");
        }

        var result = new Dictionary<(string ToolId, string ToolVersion), AgentToolCapability>();
        foreach (var node in tools)
        {
            if (node is not JsonObject tool)
            {
                throw new AgentBrokerException("invalid_capabilities");
            }

            var toolId = ReadRequiredString(tool, "tool_id", "invalid_capabilities");
            var toolVersion = ReadRequiredString(tool, "tool_version", "invalid_capabilities");
            var risk = ReadRequiredString(tool, "risk", "invalid_capabilities");
            var availability = ReadRequiredString(tool, "availability", "invalid_capabilities");
            var scopes = ReadBoundedStringSet(tool["scopes"], 16, 64, "invalid_capabilities");
            if (!ToolIdPattern.IsMatch(toolId)
                || toolId.Length > 128
                || !VersionPattern.IsMatch(toolVersion)
                || toolVersion.Length > 32
                || scopes.Count == 0
                || scopes.Any(scope => !ScopePattern.IsMatch(scope))
                || !Risks.Contains(risk)
                || !Availabilities.Contains(availability)
                || tool["model_enabled"] is not JsonValue modelValue
                || !modelValue.TryGetValue<bool>(out var modelEnabled))
            {
                throw new AgentBrokerException("invalid_capabilities");
            }

            var capability = new AgentToolCapability(
                toolId,
                toolVersion,
                scopes.ToFrozenSet(StringComparer.Ordinal),
                risk,
                availability,
                modelEnabled);
            if (!result.TryAdd((toolId, toolVersion), capability))
            {
                throw new AgentBrokerException("invalid_capabilities");
            }
        }

        return Array.AsReadOnly(result.Values
            .OrderBy(item => item.ToolId, StringComparer.Ordinal)
            .ThenBy(item => item.ToolVersion, StringComparer.Ordinal)
            .ToArray());
    }

    private void CacheCapabilities(IReadOnlyList<AgentToolCapability> snapshot)
    {
        lock (_capabilityGate)
        {
            if (_capabilities is not null)
            {
                if (!CapabilitiesEqual(_capabilities, snapshot))
                {
                    throw new AgentBrokerException("capability_snapshot_changed");
                }

                return;
            }

            _capabilities = snapshot;
            _capabilityIndex = snapshot.ToDictionary(
                item => (item.ToolId, item.ToolVersion),
                item => item);
        }
    }

    private IReadOnlyList<AgentToolCapability>? CapabilitySnapshot()
    {
        lock (_capabilityGate)
        {
            return _capabilities;
        }
    }

    private static bool CapabilitiesEqual(
        IReadOnlyList<AgentToolCapability> first,
        IReadOnlyList<AgentToolCapability> second) =>
        first.Count == second.Count
        && first.Zip(second).All(pair =>
            pair.First.ToolId == pair.Second.ToolId
            && pair.First.ToolVersion == pair.Second.ToolVersion
            && pair.First.Risk == pair.Second.Risk
            && pair.First.Availability == pair.Second.Availability
            && pair.First.ModelEnabled == pair.Second.ModelEnabled
            && pair.First.Scopes.SetEquals(pair.Second.Scopes));

    private static AgentToolResult ReadResult(AgentEnvelope envelope)
    {
        if (envelope.MessageType != AgentMessageTypes.ToolResult)
        {
            throw new AgentBrokerException("invalid_tool_result");
        }

        var status = ReadRequiredString(envelope.Payload, "status", "invalid_tool_result");
        if (!ToolResultStatuses.Known.Contains(status)
            || envelope.Payload["output"] is not JsonObject output)
        {
            throw new AgentBrokerException("invalid_tool_result");
        }

        var safeSummary = ReadRequiredString(envelope.Payload, "safe_summary", "invalid_tool_result");
        var errorCode = ReadOptionalString(envelope.Payload, "error_code", "invalid_tool_result");
        var durationMs = ReadInt(envelope.Payload, "duration_ms", "invalid_tool_result");
        var verificationState = ReadRequiredString(envelope.Payload, "verification_state", "invalid_tool_result");
        var endpointRole = ReadOptionalString(envelope.Payload, "endpoint_role", "invalid_tool_result");
        var endpointHash = ReadOptionalString(envelope.Payload, "endpoint_hash", "invalid_tool_result");
        if (safeSummary.Length > 4096
            || durationMs is < 0 or > 300_000
            || !ToolVerificationStates.Known.Contains(verificationState)
            || endpointRole is not (null or "default_render_multimedia")
            || endpointHash is not null && !EndpointHashPattern.IsMatch(endpointHash)
            || (endpointRole is null) != (endpointHash is null)
            || envelope.Payload["adapter_invoked"] is not JsonValue adapterNode
            || !adapterNode.TryGetValue<bool>(out var adapterInvoked))
        {
            throw new AgentBrokerException("invalid_tool_result");
        }

        return new AgentToolResult(
            envelope.RequestId,
            status,
            output.DeepClone().AsObject(),
            errorCode,
            safeSummary,
            durationMs,
            verificationState,
            endpointRole,
            endpointHash,
            adapterInvoked);
    }

    private static AgentToolResult Failed(string requestId, string code, string summary) =>
        new(
            requestId,
            ToolResultStatuses.Failed,
            new JsonObject(),
            code,
            summary,
            0,
            ToolVerificationStates.Failed,
            null,
            null,
            false);

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

    private static IReadOnlySet<string> ReadBoundedStringSet(
        JsonNode? node,
        int maxCount,
        int maxLength,
        string code)
    {
        if (node is not JsonArray values || values.Count > maxCount)
        {
            throw new AgentBrokerException(code);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is not JsonValue item
                || !item.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text)
                || text.Length > maxLength
                || !result.Add(text))
            {
                throw new AgentBrokerException(code);
            }
        }

        return result.ToFrozenSet(StringComparer.Ordinal);
    }

    private static string ReadRequiredString(JsonObject payload, string key, string code) =>
        payload[key] is JsonValue value
        && value.TryGetValue<string>(out var result)
        && !string.IsNullOrWhiteSpace(result)
            ? result
            : throw new AgentBrokerException(code);

    private static string? ReadOptionalString(JsonObject payload, string key, string code)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : throw new AgentBrokerException(code);
    }

    private static int ReadInt(JsonObject payload, string key, string code) =>
        payload[key] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : throw new AgentBrokerException(code);

    private static string NewRequestId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static void ValidateCall(AgentToolCall call)
    {
        if (string.IsNullOrWhiteSpace(call.RequestId)
            || call.RequestId.Length > AgentEnvelopeCodec.MaxIdLength
            || string.IsNullOrWhiteSpace(call.CorrelationId)
            || call.CorrelationId.Length > AgentEnvelopeCodec.MaxIdLength
            || !ToolIdPattern.IsMatch(call.ToolId)
            || !VersionPattern.IsMatch(call.ToolVersion)
            || string.IsNullOrWhiteSpace(call.CapabilityClass)
            || call.CapabilityClass.Length > 64
            || call.Arguments is null)
        {
            throw new ArgumentException("Tool call is invalid.", nameof(call));
        }

        ValidateDeadline(call.DeadlineMs);
    }

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
