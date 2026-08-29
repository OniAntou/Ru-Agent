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
    private static readonly IReadOnlySet<string> SkillTrustClasses = new HashSet<string>(
        ["builtin", "signed"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> SkillStepKinds = new HashSet<string>(
        ["CallTool", "ValidateResult", "Branch", "NamedTransform"],
        StringComparer.Ordinal);

    private readonly BrokerSessionDescriptor _descriptor;
    private readonly AgentSessionGrantStore _grants;
    private readonly object _capabilityGate = new();
    private readonly Dictionary<CapabilityProfile, IReadOnlyList<AgentToolCapability>> _capabilityLists = new();
    private IReadOnlyDictionary<(string ToolId, string ToolVersion), AgentToolCapability>? _capabilityIndex;
    private string? _activeSessionId;
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
        CapabilityProfile profile,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var cached = CapabilitySnapshot(profile);
        if (cached is not null)
        {
            return cached;
        }

        _ = await RoundTripAsync(
            profile,
            new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                NewRequestId("capabilities"),
                AgentMessageTypes.Health,
                CapabilitySessionId(),
                null,
                5_000,
                new JsonObject()),
            cancellationToken).ConfigureAwait(false);
        return CapabilitySnapshot(profile)
            ?? throw new AgentBrokerException("invalid_capabilities");
    }

    public async Task<IReadOnlyList<AgentSkillDescriptor>> ListSkillsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateSessionId(sessionId);
        if (!_grants.TryGet(sessionId, "skill.execute", out _))
        {
            return Array.Empty<AgentSkillDescriptor>();
        }

        var request = new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersion,
            NewRequestId("skills"),
            AgentMessageTypes.Health,
            sessionId,
            null,
            5_000,
            new JsonObject());
        var (hello, _) = await RoundTripWithHelloAsync(
            CapabilityProfile.Agent,
            request,
            cancellationToken).ConfigureAwait(false);
        return ParseSkills(hello.Payload);
    }

    public async Task<AgentSkillExecutionResult> ExecuteSkillAsync(
        string sessionId,
        string skillId,
        string version,
        JsonObject input,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateSessionId(sessionId);
        ValidateSkillIdentity(skillId, version);
        ArgumentNullException.ThrowIfNull(input);
        if (!_grants.TryGet(sessionId, "skill.execute", out var grant)
            || grant is null
            || !grant.SkillIds.Contains(skillId))
        {
            return new AgentSkillExecutionResult(
                NewRequestId("skill"), skillId, version, ToolResultStatuses.Denied,
                new JsonObject(), "skill_not_granted", "Skill is not granted.", null, 0);
        }

        var requestId = NewRequestId("skill");
        var request = new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersion,
            requestId,
            AgentMessageTypes.SkillStart,
            sessionId,
            null,
            AgentEnvelopeCodec.MaxDeadlineMs,
            new JsonObject
            {
                ["skill_id"] = skillId,
                ["version"] = version,
                ["input"] = input.DeepClone(),
            });
        return await SkillRoundTripAsync(request, cancellationToken).ConfigureAwait(false);
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
            CapabilityProfile.Chat,
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
        ResetSessionScopedCapabilityCache(sessionId);
        return grant;
    }

    public async Task<AgentToolResult> ExecuteToolAsync(
        AgentToolCall call,
        CapabilityProfile profile,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(call);
        ValidateSessionId(call.SessionId);
        ValidateCall(call);
        var capabilityIndex = CapabilityIndex();
        if (capabilityIndex is null
            || (!capabilityIndex.ContainsKey((call.ToolId, call.ToolVersion))
                && !capabilityIndex.Keys.Any(key => string.Equals(key.ToolId, call.ToolId, StringComparison.Ordinal))))
        {
            _ = await GetCapabilitiesAsync(profile, cancellationToken)
                .ConfigureAwait(false);
        }

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
            profile,
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
            CapabilityProfile.Chat,
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
            CapabilityProfile.Chat,
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
            CapabilityProfile.Chat,
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
        ClearCapabilityCache();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_capabilityGate)
        {
            _capabilityLists.Clear();
            _capabilityIndex = null;
            _activeSessionId = null;
        }

        _grants.Clear();
        return ValueTask.CompletedTask;
    }

    private async Task<AgentEnvelope> RoundTripAsync(
        CapabilityProfile profile,
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        var (_, reply) = await RoundTripWithHelloAsync(profile, request, cancellationToken).ConfigureAwait(false);
        return reply;
    }

    private async Task<(AgentEnvelope Hello, AgentEnvelope Reply)> RoundTripWithHelloAsync(
        CapabilityProfile profile,
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        var opened = await OpenSocketWithHelloAsync(profile, request, cancellationToken).ConfigureAwait(false);
        using var socket = opened.Socket;
        await SendAsync(socket, request, cancellationToken).ConfigureAwait(false);
        var reply = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(reply.RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(reply.SessionId, request.SessionId, StringComparison.Ordinal)
            || !string.Equals(reply.CorrelationId, request.CorrelationId, StringComparison.Ordinal))
        {
            throw new AgentBrokerException("stale_agent_result");
        }

        return (opened.Hello, reply);
    }

    private async Task<(ClientWebSocket Socket, AgentEnvelope Hello)> OpenSocketWithHelloAsync(
        CapabilityProfile profile,
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateDeadline(request.DeadlineMs);
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + _descriptor.AuthToken);
            await socket.ConnectAsync(GetWebSocketEndpoint(), cancellationToken).ConfigureAwait(false);
            var hello = request with
            {
                RequestId = NewRequestId("hello"),
                MessageType = AgentMessageTypes.Hello,
                Payload = new JsonObject
                {
                    ["client"] = "RuBackend",
                    ["profile"] = profile.ToWireValue(),
                },
            };
            await SendAsync(socket, hello, cancellationToken).ConfigureAwait(false);
            var helloReply = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            if (helloReply.RequestId != hello.RequestId
                || helloReply.MessageType != AgentMessageTypes.HelloOk
                || ReadInt(helloReply.Payload, "protocol_version", "invalid_hello") != AgentEnvelopeCodec.ProtocolVersion)
            {
                throw new AgentBrokerException("invalid_hello");
            }

            CacheCapabilities(profile, ParseCapabilities(helloReply.Payload));
            return (socket, helloReply);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private Uri GetWebSocketEndpoint() => new(
        _descriptor.BaseUrl.Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).TrimEnd('/')
            + "/agent");

    private async Task<AgentSkillExecutionResult> SkillRoundTripAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        var opened = await OpenSocketWithHelloAsync(CapabilityProfile.Agent, request, cancellationToken).ConfigureAwait(false);
        using var socket = opened.Socket;
        await SendAsync(socket, request, cancellationToken).ConfigureAwait(false);
        for (var frame = 0; frame < 64; frame++)
        {
            var response = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            if (response.RequestId != request.RequestId
                || response.SessionId != request.SessionId)
            {
                throw new AgentBrokerException("stale_agent_result");
            }

            if (response.MessageType == AgentMessageTypes.SkillEvent)
            {
                ValidateSkillEvent(response.Payload);
                continue;
            }

            if (response.MessageType == AgentMessageTypes.SkillResult)
            {
                return ReadSkillResult(response, request);
            }

            throw new AgentBrokerException("invalid_skill_result");
        }

        throw new AgentBrokerException("skill_event_limit");
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
            || skills.Count > 64)
        {
            throw new AgentBrokerException("invalid_capabilities");
        }

        _ = ParseSkills(payload);

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

    private static IReadOnlyList<AgentSkillDescriptor> ParseSkills(JsonObject payload)
    {
        if (payload["skills"] is not JsonArray skills || skills.Count > 64)
        {
            throw new AgentBrokerException("invalid_skills");
        }

        var result = new List<AgentSkillDescriptor>();
        foreach (var node in skills)
        {
            if (node is not JsonObject skill)
            {
                throw new AgentBrokerException("invalid_skills");
            }

            var allowedFields = new HashSet<string>(
                ["skill_id", "version", "required_scopes", "required_tools", "trust_class"],
                StringComparer.Ordinal);
            if (skill.Any(property => !allowedFields.Contains(property.Key)))
            {
                throw new AgentBrokerException("invalid_skills");
            }

            var id = ReadRequiredString(skill, "skill_id", "invalid_skills");
            var version = ReadRequiredString(skill, "version", "invalid_skills");
            var scopes = ReadBoundedStringSet(skill["required_scopes"], 16, 64, "invalid_skills");
            var tools = ReadBoundedStringSet(skill["required_tools"], 32, 128, "invalid_skills");
            var trust = ReadRequiredString(skill, "trust_class", "invalid_skills");
            if (!ToolIdPattern.IsMatch(id)
                || id.Length > 128
                || !VersionPattern.IsMatch(version)
                || version.Length > 32
                || !SkillTrustClasses.Contains(trust)
                || scopes.Count == 0
                || !scopes.Contains("skill.execute")
                || scopes.Any(scope => !ScopePattern.IsMatch(scope))
                || tools.Any(tool => !ToolIdPattern.IsMatch(tool))
                || !result.All(existing => existing.SkillId != id || existing.Version != version))
            {
                throw new AgentBrokerException("invalid_skills");
            }

            result.Add(new AgentSkillDescriptor(id, version, scopes, tools, trust));
        }

        return result
            .OrderBy(item => item.SkillId, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateSkillEvent(JsonObject payload)
    {
        var allowedFields = new HashSet<string>(
            ["status", "skill_id", "step_index", "step_kind", "step_name", "tool_id", "safe_summary"],
            StringComparer.Ordinal);
        if (payload.Any(property => !allowedFields.Contains(property.Key))
            || payload["step_index"] is not JsonValue stepValue
            || !stepValue.TryGetValue<int>(out var stepIndex)
            || stepIndex is < 0 or > 32)
        {
            throw new AgentBrokerException("invalid_skill_event");
        }

        var status = ReadRequiredString(payload, "status", "invalid_skill_event");
        var skillId = ReadRequiredString(payload, "skill_id", "invalid_skill_event");
        var stepKind = ReadRequiredString(payload, "step_kind", "invalid_skill_event");
        var stepName = ReadRequiredString(payload, "step_name", "invalid_skill_event");
        var safeSummary = ReadRequiredString(payload, "safe_summary", "invalid_skill_event");
        var toolId = ReadOptionalString(payload, "tool_id", "invalid_skill_event");
        if (!ToolResultStatuses.Known.Contains(status)
            || !ToolIdPattern.IsMatch(skillId)
            || skillId.Length > 128
            || !SkillStepKinds.Contains(stepKind)
            || stepName.Length > 128
            || safeSummary.Length > 512
            || toolId is not (null or { Length: <= 128 })
            || toolId is not null && !ToolIdPattern.IsMatch(toolId))
        {
            throw new AgentBrokerException("invalid_skill_event");
        }
    }

    private static AgentSkillExecutionResult ReadSkillResult(
        AgentEnvelope envelope,
        AgentEnvelope request)
    {
        var payload = envelope.Payload;
        var allowedFields = new HashSet<string>(
            ["status", "skill_id", "version", "run_id", "receipt_id", "state", "step_count", "safe_summary", "error_code", "output"],
            StringComparer.Ordinal);
        if (payload.Any(property => !allowedFields.Contains(property.Key)))
        {
            throw new AgentBrokerException("invalid_skill_result");
        }

        var status = ReadRequiredString(payload, "status", "invalid_skill_result");
        if (!ToolResultStatuses.Known.Contains(status))
        {
            throw new AgentBrokerException("invalid_skill_result");
        }

        var skillId = ReadRequiredString(payload, "skill_id", "invalid_skill_result");
        var version = ReadRequiredString(payload, "version", "invalid_skill_result");
        var runId = ReadRequiredString(payload, "run_id", "invalid_skill_result");
        var state = ReadRequiredString(payload, "state", "invalid_skill_result");
        var safeSummary = ReadRequiredString(payload, "safe_summary", "invalid_skill_result");
        var stepCount = ReadInt(payload, "step_count", "invalid_skill_result");
        var errorCode = ReadOptionalString(payload, "error_code", "invalid_skill_result");
        var receiptId = ReadOptionalString(payload, "receipt_id", "invalid_skill_result");
        JsonObject output;
        if (payload["output"] is JsonObject outputNode)
        {
            if (Encoding.UTF8.GetByteCount(outputNode.ToJsonString()) > 256 * 1024)
            {
                throw new AgentBrokerException("invalid_skill_result");
            }

            output = outputNode.DeepClone().AsObject();
        }
        else if (payload.ContainsKey("output"))
        {
            throw new AgentBrokerException("invalid_skill_result");
        }
        else
        {
            output = new JsonObject();
        }

        var expectedSkillId = ReadOptionalString(request.Payload, "skill_id", "invalid_skill_result");
        var expectedVersion = ReadOptionalString(request.Payload, "version", "invalid_skill_result") ?? "1.0.0";
        var knownStates = new HashSet<string>(
            ["ApprovalRequired", "Cancelled", "Completed", "Failed"],
            StringComparer.Ordinal);
        if (!ToolIdPattern.IsMatch(skillId)
            || skillId.Length > 128
            || !VersionPattern.IsMatch(version)
            || version.Length > 32
            || !string.Equals(skillId, expectedSkillId, StringComparison.Ordinal)
            || !string.Equals(version, expectedVersion, StringComparison.Ordinal)
            || runId.Length > 128
            || state.Length > 32
            || !knownStates.Contains(state)
            || safeSummary.Length > 512
            || stepCount is < 0 or > 64
            || errorCode is not (null or { Length: <= 64 })
            || errorCode is not null && !Regex.IsMatch(errorCode, "^[a-z][a-z0-9_]{0,63}$")
            || receiptId is not (null or { Length: >= 1 and <= 128 }))
        {
            throw new AgentBrokerException("invalid_skill_result");
        }

        return new AgentSkillExecutionResult(
            envelope.RequestId,
            skillId,
            version,
            status,
            output,
            errorCode,
            safeSummary,
            receiptId,
            stepCount);
    }

    private void CacheCapabilities(
        CapabilityProfile profile,
        IReadOnlyList<AgentToolCapability> snapshot)
    {
        lock (_capabilityGate)
        {
            if (_capabilityLists.TryGetValue(profile, out var existing))
            {
                if (!CapabilitiesEqual(existing, snapshot))
                {
                    throw new AgentBrokerException("capability_snapshot_changed");
                }

                return;
            }

            var index = new Dictionary<(string ToolId, string ToolVersion), AgentToolCapability>(
                _capabilityIndex ?? new Dictionary<(string, string), AgentToolCapability>());
            foreach (var capability in snapshot)
            {
                if (index.TryGetValue(
                        (capability.ToolId, capability.ToolVersion),
                        out var merged)
                    && !CapabilitiesEqual([merged], [capability]))
                {
                    throw new AgentBrokerException("capability_snapshot_changed");
                }

                index[(capability.ToolId, capability.ToolVersion)] = capability;
            }

            _capabilityLists[profile] = snapshot;
            _capabilityIndex = index;
        }
    }

    private void ClearCapabilityCache()
    {
        lock (_capabilityGate)
        {
            _capabilityLists.Clear();
            _capabilityIndex = null;
            _activeSessionId = null;
        }
    }

    private void ResetSessionScopedCapabilityCache(string sessionId)
    {
        lock (_capabilityGate)
        {
            _activeSessionId = sessionId;
            foreach (var profile in _capabilityLists.Keys
                         .Where(profile => profile != CapabilityProfile.Chat)
                         .ToArray())
            {
                _capabilityLists.Remove(profile);
            }

            _capabilityIndex = BuildCapabilityIndex(_capabilityLists.Values.SelectMany(snapshot => snapshot));
        }
    }

    private string CapabilitySessionId()
    {
        lock (_capabilityGate)
        {
            return _activeSessionId ?? "capabilities";
        }
    }

    private static IReadOnlyDictionary<(string ToolId, string ToolVersion), AgentToolCapability> BuildCapabilityIndex(
        IEnumerable<AgentToolCapability> capabilities)
    {
        var index = new Dictionary<(string ToolId, string ToolVersion), AgentToolCapability>();
        foreach (var capability in capabilities)
        {
            index[(capability.ToolId, capability.ToolVersion)] = capability;
        }

        return index;
    }

    private IReadOnlyList<AgentToolCapability>? CapabilitySnapshot(CapabilityProfile profile)
    {
        lock (_capabilityGate)
        {
            return _capabilityLists.TryGetValue(profile, out var snapshot) ? snapshot : null;
        }
    }

    private IReadOnlyDictionary<(string ToolId, string ToolVersion), AgentToolCapability>? CapabilityIndex()
    {
        lock (_capabilityGate)
        {
            return _capabilityIndex;
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

    private static void ValidateSkillIdentity(string skillId, string version)
    {
        if (string.IsNullOrWhiteSpace(skillId)
            || skillId.Length > 128
            || !ToolIdPattern.IsMatch(skillId))
        {
            throw new ArgumentException("Skill id is invalid.", nameof(skillId));
        }

        if (string.IsNullOrWhiteSpace(version)
            || version.Length > 32
            || !VersionPattern.IsMatch(version))
        {
            throw new ArgumentException("Skill version is invalid.", nameof(version));
        }
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
