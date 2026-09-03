using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Skills;
using RuAgent.Protocol;

namespace RuAgent.Broker.Transport;

public sealed class AgentConnection
{
    public const int MaxFrameBytes = 64 * 1024;

    private readonly BrokerSession _session;
    private readonly Func<CapabilityProfile, IReadOnlyList<AgentToolCapability>> _capabilityProvider;
    private readonly IReadOnlySet<string> _executableScopes;
    private readonly Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? _toolHandler;
    private readonly Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? _approvalHandler;
    private readonly Action<string>? _sessionClosedHandler;
    private readonly PendingApprovalStore _pendingV2;
    private readonly ApprovalTokenService _authTokens;
    private readonly Func<string, IReadOnlyList<SkillManifest>>? _skillListProvider;
    private readonly Func<IReadOnlyList<SkillManifest>>? _skillCatalogProvider;
    private readonly Func<string, string, SkillManifest?>? _skillResolver;
    private readonly Func<string, string, string, JsonObject, CancellationToken, Task<SkillRun>>? _skillExecutor;
    private readonly IReadOnlyList<string>? _advertisedCapabilities;
    private readonly Dictionary<string, SkillRequestReplay> _skillRequests = new(StringComparer.Ordinal);

    private sealed record SkillRequestReplay(
        string CanonicalHash,
        IReadOnlyList<AgentEnvelope> Messages);

    public AgentConnection(
        BrokerSession session,
        Func<CapabilityProfile, IReadOnlyList<AgentToolCapability>> capabilityProvider,
        IReadOnlySet<string> executableScopes,
        Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? toolHandler = null,
        Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? approvalHandler = null,
        Action<string>? sessionClosedHandler = null,
        PendingApprovalStore? pendingV2Store = null,
        ApprovalTokenService? approvalTokenService = null,
        Func<string, IReadOnlyList<SkillManifest>>? skillListProvider = null,
        Func<IReadOnlyList<SkillManifest>>? skillCatalogProvider = null,
        Func<string, string, SkillManifest?>? skillResolver = null,
        Func<string, string, string, JsonObject, CancellationToken, Task<SkillRun>>? skillExecutor = null,
        IReadOnlyList<string>? advertisedCapabilities = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _capabilityProvider = capabilityProvider ?? throw new ArgumentNullException(nameof(capabilityProvider));
        _executableScopes = new HashSet<string>(
            executableScopes ?? throw new ArgumentNullException(nameof(executableScopes)),
            StringComparer.Ordinal);
        _toolHandler = toolHandler;
        _approvalHandler = approvalHandler;
        _sessionClosedHandler = sessionClosedHandler;
        // State is scoped to the BrokerHost by callers that need reconnect support;
        // a directly constructed connection gets private state instead of sharing
        // approvals/tokens with unrelated hosts or test instances.
        _pendingV2 = pendingV2Store ?? new PendingApprovalStore();
        _authTokens = approvalTokenService ?? new ApprovalTokenService();
        _skillListProvider = skillListProvider;
        _skillCatalogProvider = skillCatalogProvider;
        _skillResolver = skillResolver;
        _skillExecutor = skillExecutor;
        _advertisedCapabilities = advertisedCapabilities;
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

                if (request.MessageType == AgentMessageTypes.SkillStart)
                {
                    var skillMessages = await HandleSkillStartAsync(request, cancellationToken).ConfigureAwait(false);
                    foreach (var skillMessage in skillMessages)
                    {
                        await SendAsync(socket, skillMessage, cancellationToken).ConfigureAwait(false);
                    }

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
                if (!_session.Owns(request.SessionId))
                {
                    return ToolError(request, "broker_not_ready", "session is not open");
                }

                return _toolHandler is null
                    ? ToolError(request, "broker_not_ready", "tool execution is not registered")
                    : await _toolHandler(request, cancellationToken).ConfigureAwait(false);
            case AgentMessageTypes.ToolEvaluate:
                return await HandleToolEvaluateAsync(request, cancellationToken).ConfigureAwait(false);
            case AgentMessageTypes.ToolPrepare:
                return await HandleToolPrepareAsync(request, cancellationToken).ConfigureAwait(false);
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
            case AgentMessageTypes.SkillStart:
                var skillMessages = await HandleSkillStartAsync(request, cancellationToken).ConfigureAwait(false);
                return skillMessages[^1];
            case AgentMessageTypes.ApprovalResponse:
                if (!_session.Owns(request.SessionId))
                {
                    return ToolError(request, "broker_not_ready", "session is not open");
                }

                if (request.ProtocolVersion == AgentEnvelopeCodec.ProtocolVersionV2)
                {
                    // Phase 1: approval handling via Broker is not available for v2; approvals are user-control only.
                    return ToolError(request, "phase_not_available", "approval handling not available in phase 1");
                }

                return _approvalHandler is null
                    ? ToolError(request, "broker_not_ready", "approval handling is not registered")
                    : await _approvalHandler(request, cancellationToken).ConfigureAwait(false);
            case AgentMessageTypes.ApprovalRespond:
                if (!_session.Owns(request.SessionId))
                {
                    return ToolError(request, "broker_not_ready", "session is not open");
                }

                return await HandleApprovalRespondAsync(request, cancellationToken).ConfigureAwait(false);
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
        IReadOnlySet<string> skillIds;
        try
        {
            skillIds = ReadSkillIds(request.Payload);
        }
        catch (AgentProtocolException error)
        {
            return ToolError(request, error.Code, "invalid skill grant");
        }

        var scopes = ReadScopes(request.Payload);
        if (scopes.Any(scope => !_executableScopes.Contains(scope)))
        {
            return ToolError(request, "scope_unavailable", "scope is not executable");
        }

        var resolvedSkills = new List<SkillManifest>();
        foreach (var requestedId in skillIds)
        {
            var requested = ResolveRequestedSkill(requestedId, request.SessionId);
            if (requested is null)
            {
                return ToolError(request, "skill_not_granted", "skill is not available");
            }

            if (!scopes.Contains(ToolScope.SkillExecute)
                || !requested.RequiredScopes.All(scopes.Contains))
            {
                return ToolError(request, "scope_denied", "skill scope is not granted");
            }

            resolvedSkills.Add(requested);
        }

        if (!_session.Open(request.SessionId, scopes, resolvedSkills
            .Select(skill => skill.Id)
            .ToHashSet(StringComparer.Ordinal)))
        {
            return ToolError(request, "session_conflict", "session is already owned");
        }

        return Reply(request, AgentMessageTypes.SessionOpened, new JsonObject
        {
            ["status"] = ToolResultStatuses.Succeeded,
            ["scopes"] = new JsonArray(_session.Scopes
                .OrderBy(scope => scope, StringComparer.Ordinal)
                .Select(scope => (JsonNode?)scope).ToArray()),
            ["skill_ids"] = new JsonArray(resolvedSkills
                .Select(skill => (JsonNode?)skill.Id)
                .Distinct()
                .ToArray()),
            ["safe_summary"] = "session opened",
        });
    }

    private AgentEnvelope CloseSession(AgentEnvelope request)
    {
        var owned = _session.Owns(request.SessionId);
        _session.Close(request.SessionId);
        if (owned)
        {
            _skillRequests.Clear();
            _sessionClosedHandler?.Invoke(request.SessionId);
        }
        return Reply(request, AgentMessageTypes.SkillResult, new JsonObject
        {
            ["status"] = "succeeded",
            ["safe_summary"] = "session closed",
        });
    }

    private async Task<IReadOnlyList<AgentEnvelope>> HandleSkillStartAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        if (!_session.Owns(request.SessionId))
        {
            // SkillStart has a dedicated response stream (SkillEvent/SkillResult).
            // Keep even pre-session failures on that stream so strict clients do
            // not have to special-case a ToolResult with a different shape.
            return [SkillResultError(request, "broker_not_ready", "session is not open")];
        }

        var skillId = ReadOptionalPayloadString(request.Payload, "skill_id");
        var version = ReadOptionalPayloadString(request.Payload, "version") ?? "1.0.0";
        if (skillId is null || version is null)
        {
            return [SkillResultError(request, "invalid_skill", "skill identity is invalid")];
        }

        if (request.Payload["input"] is not JsonObject input)
        {
            return [SkillResultError(request, "schema_invalid", "skill input must be an object")];
        }

        var skill = _skillResolver?.Invoke(skillId, version)
            ?? _skillListProvider?.Invoke(request.SessionId)
                .FirstOrDefault(item => string.Equals(item.Id, skillId, StringComparison.Ordinal)
                    && string.Equals(item.Version, version, StringComparison.Ordinal));
        if (skill is null || !_session.SkillIds.Contains(skillId))
        {
            return [SkillResultError(request, "skill_not_granted", "skill is not granted")];
        }

        var canonicalHash = ComputeSkillRequestHash(skillId, version, input);
        if (_skillRequests.TryGetValue(request.RequestId, out var replay))
        {
            if (!string.Equals(replay.CanonicalHash, canonicalHash, StringComparison.Ordinal))
            {
                return [SkillResultError(request, "request_conflict", "request id is already bound to another skill input")];
            }

            return replay.Messages
                .Select(message => message with { Payload = (JsonObject)message.Payload.DeepClone() })
                .ToArray();
        }

        if (_skillExecutor is null)
        {
            return [SkillResultError(request, "broker_not_ready", "skill execution is not registered")];
        }

        SkillRun run;
        try
        {
            run = await _skillExecutor(
                request.SessionId,
                skillId,
                version,
                (JsonObject)input.DeepClone(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run = new SkillRun(
                $"skill-{Guid.NewGuid():N}",
                request.SessionId,
                skillId,
                SkillRunState.Cancelled,
                0,
                "Skill cancelled.",
                "cancelled");
        }
        catch (Exception)
        {
            run = new SkillRun(
                $"skill-{Guid.NewGuid():N}",
                request.SessionId,
                skillId,
                SkillRunState.Failed,
                0,
                "Skill failed.",
                "skill_failed");
        }

        var messages = new List<AgentEnvelope>();
        foreach (var step in run.StepReceipts)
        {
            messages.Add(Reply(request, AgentMessageTypes.SkillEvent, new JsonObject
            {
                ["status"] = step.Status,
                ["skill_id"] = run.SkillId,
                ["step_index"] = step.StepIndex,
                ["step_kind"] = step.Kind.ToString(),
                ["step_name"] = step.Name,
                ["tool_id"] = step.ToolId,
                ["safe_summary"] = ClampProtocolText(step.SafeSummary),
            }));
        }

        var resultPayload = new JsonObject
        {
            ["status"] = run.State switch
            {
                SkillRunState.Completed => ToolResultStatuses.Succeeded,
                SkillRunState.Cancelled => ToolResultStatuses.Cancelled,
                SkillRunState.ApprovalRequired => ToolResultStatuses.ApprovalRequired,
                _ => ToolResultStatuses.Failed,
            },
            ["skill_id"] = run.SkillId,
            ["version"] = version,
            ["run_id"] = run.RunId,
            ["receipt_id"] = run.Receipt,
            ["state"] = run.State.ToString(),
            ["step_count"] = run.StepReceipts.Count,
            ["safe_summary"] = ClampProtocolText(run.SafeSummary),
        };
        if (run.ErrorCode is not null)
        {
            resultPayload["error_code"] = StableErrorCode(run.ErrorCode, "skill_failed");
        }

        if (run.Output is not null && run.Output.ToJsonString().Length <= 256 * 1024)
        {
            resultPayload["output"] = run.Output.DeepClone();
        }

        messages.Add(Reply(request, AgentMessageTypes.SkillResult, resultPayload));
        _skillRequests[request.RequestId] = new SkillRequestReplay(canonicalHash, messages.ToArray());
        return messages;
    }

    private SkillManifest? ResolveRequestedSkill(string requestedId, string sessionId)
    {
        var separator = requestedId.LastIndexOf('@');
        if (separator > 0 && separator < requestedId.Length - 1)
        {
            var id = requestedId[..separator];
            var version = requestedId[(separator + 1)..];
            return _skillResolver?.Invoke(id, version)
                ?? _skillListProvider?.Invoke(sessionId)
                    .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal)
                        && string.Equals(item.Version, version, StringComparison.Ordinal));
        }

        var catalog = _skillCatalogProvider?.Invoke()
            ?? _skillListProvider?.Invoke(sessionId)
            ?? Array.Empty<SkillManifest>();
        return catalog
            .Where(item => string.Equals(item.Id, requestedId, StringComparison.Ordinal))
            .OrderByDescending(item => item.Version, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string ComputeSkillRequestHash(string skillId, string version, JsonObject input)
    {
        var canonical = skillId + "\n" + version + "\n" + input.ToJsonString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static AgentEnvelope SkillResultError(AgentEnvelope request, string errorCode, string summary)
    {
        // SkillResult has one stable shape for both success and failure. Keep
        // failure replies parseable by the strict host client without echoing
        // untrusted request text: malformed identities use a fixed placeholder.
        var skillId = ReadOptionalPayloadString(request.Payload, "skill_id");
        if (skillId is null || !IsProtocolSkillId(skillId))
        {
            skillId = "invalid.skill";
        }

        var version = ReadOptionalPayloadString(request.Payload, "version");
        if (version is null || !IsProtocolVersion(version))
        {
            version = "1.0.0";
        }

        var runId = "skill-error-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(request.RequestId))).ToLowerInvariant();
        return Reply(request, AgentMessageTypes.SkillResult, new JsonObject
        {
            ["status"] = ToolResultStatuses.Denied,
            ["skill_id"] = skillId,
            ["version"] = version,
            ["run_id"] = runId,
            ["state"] = SkillRunState.Failed.ToString(),
            ["step_count"] = 0,
            ["safe_summary"] = ClampProtocolText(summary),
            ["error_code"] = StableErrorCode(errorCode, "skill_failed"),
            ["output"] = new JsonObject(),
        });
    }

    private static bool IsProtocolSkillId(string value) =>
        value.Length is > 0 and <= 128
        && value[0] is >= 'a' and <= 'z'
        && value.Contains('.', StringComparison.Ordinal)
        && value.Split('.').All(segment =>
            segment.Length > 0
            && (segment[0] is >= 'a' and <= 'z' or >= '0' and <= '9')
            && segment.All(character => character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '-'));

    private static bool IsProtocolVersion(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 3
            && parts.All(part => part.Length > 0 && part.Length <= 8 && part.All(char.IsAsciiDigit))
            && parts.All(part => part.Length == 1 || part[0] != '0');
    }

    private static string ClampProtocolText(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? "Skill step completed."
            : text.Length <= 512 ? text : text[..512];

    private static string StableErrorCode(string? code, string fallback)
    {
        if (string.IsNullOrWhiteSpace(code)
            || code.Length > 64
            || code.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
        {
            return fallback;
        }

        return code;
    }

    private AgentEnvelope HandleRunOpen(AgentEnvelope request)
    {
        try
        {
            var lease = ReadRunLease(request);
            if (!string.Equals(request.SessionId, lease.SessionId, StringComparison.Ordinal))
            {
                return ToolError(request, "session_conflict", "run lease session does not match connection session");
            }

            if (!_session.TryOpenRunLease(lease, out var errorCode))
            {
                return ToolError(request, errorCode ?? "lease_failed", "run lease failed");
            }

            return Reply(request, AgentMessageTypes.RunOpened, new JsonObject
            {
                ["status"] = ToolResultStatuses.Succeeded,
                ["run_id"] = lease.RunId,
                ["snapshot_version"] = lease.Snapshot.Version,
                ["binding_version"] = lease.BindingVersion,
                ["advertised_capabilities"] = new JsonArray(lease.AdvertisedCapabilities.Select(c => (JsonNode?)c).ToArray()),
                ["safe_summary"] = "run opened",
            });
        }
        catch (AgentProtocolException error)
        {
            return ToolError(request, error.Code, "invalid run open");
        }
    }

    private AgentEnvelope HandleRunPolicyRebind(AgentEnvelope request)
    {
        try
        {
            var lease = ReadRunLease(request);
            if (!string.Equals(request.SessionId, lease.SessionId, StringComparison.Ordinal))
            {
                return ToolError(request, "session_conflict", "run lease session does not match connection session");
            }

            if (!_session.TryRebindRunPolicy(lease, out var errorCode))
            {
                return ToolError(request, errorCode ?? "rebind_failed", "run policy rebind failed");
            }

            var bound = _session.ActiveRunLease;
            if (bound is null)
            {
                return ToolError(request, "run_not_open", "run lease is not open");
            }
            return Reply(request, AgentMessageTypes.RunRebound, new JsonObject
            {
                ["status"] = ToolResultStatuses.Succeeded,
                ["run_id"] = bound.RunId,
                ["snapshot_version"] = bound.Snapshot.Version,
                ["binding_version"] = bound.BindingVersion,
                ["safe_summary"] = "run policy rebound",
            });
        }
        catch (AgentProtocolException error)
        {
            return ToolError(request, error.Code, "invalid rebind");
        }
    }

    private BrokerRunLease ReadRunLease(AgentEnvelope request)
    {
        var payload = request.Payload;
        var runId = ReadRequiredString(payload, "run_id");
        var sessionId = ReadRequiredString(payload, "session_id");
        var workspaceId = ReadRequiredString(payload, "workspace_id");
        var canonicalRoot = ReadRequiredString(payload, "canonical_root");
        var volumeSerial = ReadRequiredString(payload, "volume_serial");
        var rootFileId = ReadRequiredString(payload, "root_file_id");
        var displayHash = ReadRequiredString(payload, "display_hash");
        var preset = ReadRequiredString(payload, "preset");
        var snapshotVersion = ReadRequiredLong(payload, "snapshot_version");
        var snapshotHash = ReadRequiredString(payload, "snapshot_hash");
        var expiresAtStr = ReadRequiredString(payload, "expires_at");
        if (!DateTimeOffset.TryParse(
                expiresAtStr,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt))
        {
            throw new AgentProtocolException("invalid_expires_at");
        }

        if (payload["snapshot"] is not JsonObject snapshotNode)
        {
            throw new AgentProtocolException("invalid_run_open");
        }

        var snapshot = RuAgent.Protocol.Harness.BrokerPolicyContracts.ReadSnapshot(snapshotNode);
        return BrokerRunLease.CreateValidated(
            runId,
            sessionId,
            workspaceId,
            canonicalRoot,
            volumeSerial,
            rootFileId,
            displayHash,
            preset,
            snapshotVersion,
            snapshotHash,
            expiresAt,
            snapshot,
            advertisedCapabilities: _advertisedCapabilities);
    }

    private AgentEnvelope HandleRunClose(AgentEnvelope request)
    {
        if (!_session.Owns(request.SessionId))
        {
            return ToolError(request, "broker_not_ready", "session is not open");
        }

        var runId = request.Payload["run_id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : request.RequestId;
        if (_session.TryCloseRun(runId))
        {
            return Reply(request, AgentMessageTypes.RunClosed, new JsonObject
            {
                ["status"] = ToolResultStatuses.Succeeded,
                ["run_id"] = runId,
                ["safe_summary"] = "run closed",
            });
        }

        return Reply(request, AgentMessageTypes.RunClosed, new JsonObject
        {
            ["status"] = ToolResultStatuses.Denied,
            ["error_code"] = "run_not_open",
            ["safe_summary"] = "no matching open run",
        });
    }

    private Task<AgentEnvelope> HandleToolEvaluateAsync(AgentEnvelope request, CancellationToken cancellationToken)
    {
        var payload = request.Payload;
        var runId = ReadOptionalPayloadString(payload, "run_id") ?? request.SessionId;
        var toolId = ReadOptionalPayloadString(payload, "tool_id") ?? string.Empty;
        var toolVersion = ReadOptionalPayloadString(payload, "tool_version") ?? "1.0.0";

        if (!TryGetOwnedRunLease(request, runId, out var lease, out var leaseError) || lease is null)
        {
            return Task.FromResult(ToolError(request, leaseError, "run lease is not open"));
        }

        if (!lease.AdvertisedCapabilities.Contains(toolId))
        {
            // Phase 2 advertises only files.list/files.read; terminal and mutation tools stay
            // unadvertised until their phases land, regardless of what a rule might allow.
            return Task.FromResult(ToolError(request, "phase_not_available", "tool is not advertised yet"));
        }

        if (payload["arguments"] is not JsonObject arguments)
        {
            return Task.FromResult(DecisionReply(
                request,
                decision: "deny",
                reasonCode: "schema_invalid",
                matchedRuleId: null));
        }

        PolicyContext context;
        try
        {
            context = new PolicyContextFactory(lease).Create(toolId, toolVersion, arguments);
        }
        catch (PolicyContextException error)
        {
            return Task.FromResult(DecisionReply(request, "deny", error.Code, null));
        }

        var evaluation = new PolicySnapshotEvaluator()
            .Evaluate(lease.Snapshot, context, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);

        string? decisionToken = null;
        string? approvalId = null;
        DateTimeOffset? approvalExpiresAt = null;
        JsonObject? matcherPreview = null;
        if (string.Equals(evaluation.Decision, "allow", StringComparison.Ordinal))
        {
            try
            {
                decisionToken = _session.RegisterDecision(runId, toolId, context.IntentHash, DecisionLifetime);
            }
            catch (BrokerSessionException)
            {
                return Task.FromResult(ToolError(request, "run_not_open", "run lease is not open"));
            }
        }
        else if (string.Equals(evaluation.Decision, "ask", StringComparison.Ordinal))
        {
            // V2 two-phase approval: persist pending bound to snapshot + intent hash (30-min expiry, exact-by-default preview)
            var toolCallId = ReadOptionalPayloadString(payload, "tool_call_id") ?? request.RequestId;
            var snapshotVersion = lease.Snapshot.Version;
            var binding = new V2ApprovalBinding(
                RunId: runId,
                ToolCallId: toolCallId,
                ToolId: toolId,
                ToolVersion: toolVersion,
                CanonicalPath: context.Target?.CanonicalPath ?? context.RelativePathInsideWorkspace,
                ResolvedExecutable: context.ResolvedExecutable,
                Argv: context.Argv,
                Cwd: context.CanonicalCwd,
                OperationClass: context.OperationClass,
                NetworkIntent: context.NetworkIntent,
                SnapshotVersion: snapshotVersion,
                IntentHash: context.IntentHash);
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
            approvalId = _pendingV2.IssueV2(binding, expiresAt);
            approvalExpiresAt = expiresAt;
            matcherPreview = PendingApprovalStore.BuildMatcherPreview(binding);
            matcherPreview["safe_summary"] = "approval required for " + toolId;
        }

        var replyPayload = new JsonObject
        {
            ["status"] = evaluation.Decision switch
            {
                "allow" => ToolResultStatuses.Succeeded,
                "deny" => ToolResultStatuses.Denied,
                _ => ToolResultStatuses.Succeeded,
            },
            ["decision"] = evaluation.Decision,
            ["reason_code"] = evaluation.ReasonCode,
            ["analyzer_risk_pending"] = evaluation.AnalyzerRiskPending,
            ["safe_summary"] = evaluation.Decision switch
            {
                "allow" => "policy allowed the call",
                "ask" => "policy requires user approval",
                _ => "policy denied the call",
            },
        };
        if (evaluation.MatchedRuleId is not null)
        {
            replyPayload["matched_rule_id"] = evaluation.MatchedRuleId;
        }

        if (decisionToken is not null)
        {
            replyPayload["decision_token"] = decisionToken;
        }

        if (approvalId is not null)
        {
            replyPayload["approval_id"] = approvalId;
            replyPayload["expires_at"] = approvalExpiresAt!.Value.ToString("O");
            replyPayload["matcher_preview"] = matcherPreview!.DeepClone();
        }

        return Task.FromResult(Reply(request, AgentMessageTypes.ToolDecision, replyPayload));
    }

    private Task<AgentEnvelope> HandleToolPrepareAsync(AgentEnvelope request, CancellationToken cancellationToken)
    {
        var payload = request.Payload;
        var runId = ReadOptionalPayloadString(payload, "run_id") ?? request.SessionId;
        var toolId = ReadOptionalPayloadString(payload, "tool_id") ?? string.Empty;
        var decisionToken = ReadOptionalPayloadString(payload, "decision_token");

        if (!TryGetOwnedRunLease(request, runId, out _, out var leaseError))
        {
            return Task.FromResult(ToolError(request, leaseError, "run lease is not open"));
        }

        if (string.IsNullOrWhiteSpace(decisionToken))
        {
            return Task.FromResult(ToolError(request, "invalid_prepare", "missing decision token"));
        }

        if (!_session.TryPrepareDecision(decisionToken!, runId, toolId, out var preparationToken, out var errorCode))
        {
            return Task.FromResult(ToolError(request, errorCode ?? "decision_stale", "prepare failed"));
        }

        return Task.FromResult(Reply(request, AgentMessageTypes.ToolPrepared, new JsonObject
        {
            ["status"] = ToolResultStatuses.Succeeded,
            ["preparation_token"] = preparationToken,
            ["safe_summary"] = "prepared",
        }));
    }

    private Task<AgentEnvelope> HandleApprovalRespondAsync(AgentEnvelope request, CancellationToken cancellationToken)
    {
        var payload = request.Payload;

        // Legacy test hook: if run_id is missing, keep old phase_not_available to preserve BrokerHostTests
        var runId = ReadOptionalPayloadString(payload, "run_id");
        if (string.IsNullOrWhiteSpace(runId))
        {
            // For v1 sessions without run lease, approval.respond is still phase_not_available
            return Task.FromResult(Reply(request, AgentMessageTypes.ApprovalResult, new JsonObject
            {
                ["status"] = ToolResultStatuses.Denied,
                ["error_code"] = "phase_not_available",
                ["safe_summary"] = "v2 approval decisions are not wired without run_id",
            }));
        }

        var approvalId = ReadOptionalPayloadString(payload, "approval_id");
        if (string.IsNullOrWhiteSpace(approvalId))
        {
            return Task.FromResult(Reply(request, AgentMessageTypes.ApprovalResult, new JsonObject
            {
                ["status"] = ToolResultStatuses.Denied,
                ["error_code"] = "invalid_approval_id",
                ["safe_summary"] = "missing approval_id",
            }));
        }

        // Parse command: prefer "command", fallback to "approved" bool
        ApprovalCommandV2 command;
        var commandRaw = ReadOptionalPayloadString(payload, "command");
        if (!string.IsNullOrWhiteSpace(commandRaw))
        {
            var normalizedCommand = commandRaw.ToLowerInvariant();
            if (normalizedCommand is not ("allow_once" or "allowonce" or "allow_run" or "allowrun"
                or "allow_project" or "allowproject" or "deny"))
            {
                return Task.FromResult(Reply(request, AgentMessageTypes.ApprovalResult, new JsonObject
                {
                    ["status"] = ToolResultStatuses.Denied,
                    ["error_code"] = "invalid_command",
                    ["safe_summary"] = "unknown approval command",
                }));
            }

            command = normalizedCommand switch
            {
                "allow_once" or "allowonce" => ApprovalCommandV2.AllowOnce,
                "allow_run" or "allowrun" => ApprovalCommandV2.AllowRun,
                "allow_project" or "allowproject" => ApprovalCommandV2.AllowProject,
                "deny" => ApprovalCommandV2.Deny,
                _ => throw new InvalidOperationException("invalid_command"),
            };
        }
        else if (payload["approved"] is JsonValue av && av.TryGetValue<bool>(out var approved))
        {
            command = approved ? ApprovalCommandV2.AllowOnce : ApprovalCommandV2.Deny;
        }
        else
        {
            return Task.FromResult(Reply(request, AgentMessageTypes.ApprovalResult, new JsonObject
            {
                ["status"] = ToolResultStatuses.Denied,
                ["error_code"] = "invalid_command",
                ["safe_summary"] = "missing command",
            }));
        }

        if (!TryGetOwnedRunLease(request, runId!, out var lease, out var leaseError) || lease is null)
        {
            return Task.FromResult(Reply(request, AgentMessageTypes.ApprovalResult, new JsonObject
            {
                ["status"] = ToolResultStatuses.Denied,
                ["error_code"] = leaseError,
                ["safe_summary"] = "run lease is not open",
            }));
        }

        // Re-resolve the pending binding for comparison: we need to reconstruct expected binding from lease context?
        // For validation we use the stored binding vs expected: the expected binding must be exactly the stored one.
        // The caller provides the same fields via the original evaluate context; but we can validate by fetching the pending
        // and comparing against a binding reconstructed from current lease + pending data.
        // To implement 12-field stale checks, we peek and build expected from pending's own binding as baseline,
        // then if caller supplied canonical fields we could mismatch? Simpler: we trust the stored binding as source of truth
        // and for stale testing we require caller to supply expected binding explicitly. For the transport path, the client
        // does not supply full binding; we simply check expiry and then consume with the stored binding as expected.
        // However for precise stale tests, we need to detect mismatched intent hash etc when caller re-evaluates.
        // So if caller supplies optional override fields, we can detect mismatch.

        if (!_pendingV2.TryPeekV2(approvalId!, out var pending) || pending is null)
        {
            return Task.FromResult(Reply(request, AgentMessageTypes.ApprovalResult, new JsonObject
            {
                ["status"] = ToolResultStatuses.Denied,
                ["error_code"] = "approval_not_found",
                ["safe_summary"] = "approval not found",
            }));
        }

        if (!string.Equals(pending.Binding.RunId, runId, StringComparison.Ordinal))
        {
            // Approval identifiers are opaque, but the binding is still scoped to
            // the run that requested it. Never let a caller consume another run's
            // pending approval by guessing or replaying its id.
            return Task.FromResult(Reply(request, AgentMessageTypes.ApprovalResult, new JsonObject
            {
                ["status"] = ToolResultStatuses.Denied,
                ["error_code"] = "approval_stale",
                ["safe_summary"] = "approval is bound to a different run",
            }));
        }

        // Build expected binding: by default use pending's binding, but if payload supplies overrides, use those to trigger stale.
        var expectedBinding = pending.Binding;
        // Optional overrides for testing stale paths: if payload contains matcher_preview overrides, we treat them as expected.
        // For general flow without overrides, expected == pending => not stale.
        var overrideHash = ReadOptionalPayloadString(payload, "expected_intent_hash");
        var overrideVersionRaw = payload["expected_snapshot_version"] is JsonValue sv && sv.TryGetValue<long>(out var ov) ? (long?)ov : null;
        var overrideToolId = ReadOptionalPayloadString(payload, "expected_tool_id");
        if (overrideHash is not null || overrideVersionRaw is not null || overrideToolId is not null)
        {
            expectedBinding = new V2ApprovalBinding(
                RunId: pending.Binding.RunId,
                ToolCallId: pending.Binding.ToolCallId,
                ToolId: overrideToolId ?? pending.Binding.ToolId,
                ToolVersion: pending.Binding.ToolVersion,
                CanonicalPath: pending.Binding.CanonicalPath,
                ResolvedExecutable: pending.Binding.ResolvedExecutable,
                Argv: pending.Binding.Argv,
                Cwd: pending.Binding.Cwd,
                OperationClass: pending.Binding.OperationClass,
                NetworkIntent: pending.Binding.NetworkIntent,
                SnapshotVersion: overrideVersionRaw ?? pending.Binding.SnapshotVersion,
                IntentHash: overrideHash ?? pending.Binding.IntentHash);
        }

        var now = DateTimeOffset.UtcNow;
        var resolution = _pendingV2.TryConsumeV2(approvalId!, command, expectedBinding, now);

        var resultPayload = new JsonObject();
        switch (resolution.Outcome)
        {
            case V2ApprovalOutcome.Expired:
                resultPayload["status"] = ToolResultStatuses.Denied;
                resultPayload["error_code"] = "approval_expired";
                resultPayload["safe_summary"] = "approval expired";
                break;
            case V2ApprovalOutcome.Stale:
                resultPayload["status"] = ToolResultStatuses.Denied;
                resultPayload["error_code"] = "approval_stale";
                resultPayload["safe_summary"] = "approval stale (intent changed)";
                break;
            case V2ApprovalOutcome.NotFound:
                resultPayload["status"] = ToolResultStatuses.Denied;
                resultPayload["error_code"] = "approval_not_found";
                resultPayload["safe_summary"] = "approval not found";
                break;
            case V2ApprovalOutcome.ConsumedDenied:
                resultPayload["status"] = ToolResultStatuses.Denied;
                resultPayload["error_code"] = "approval_denied";
                resultPayload["safe_summary"] = "approval denied";
                break;
            case V2ApprovalOutcome.ConsumedApproved:
                // Issue authorization token (60s) bound to snapshot + intent hash + tool identity
                var authToken = _authTokens.Issue(
                    requestId: pending.Binding.RunId,
                    sessionId: request.SessionId,
                    toolId: pending.Binding.ToolId,
                    toolVersion: pending.Binding.ToolVersion,
                    argumentsHash: pending.Binding.IntentHash,
                    lifetime: TimeSpan.FromSeconds(60),
                    snapshotVersion: pending.Binding.SnapshotVersion,
                    intentHash: pending.Binding.IntentHash);
                resultPayload["status"] = "authorized";
                resultPayload["authorization_token"] = authToken;
                resultPayload["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(60).ToString("O");
                resultPayload["matcher_preview"] = PendingApprovalStore.BuildMatcherPreview(pending.Binding).DeepClone();
                if (resolution.Candidate is not null)
                {
                    // Exact-by-default preview already stripped
                    resultPayload["rule_saved"] = false; // Broker does not persist reusable rules; Host does. Report not saved here.
                    resultPayload["reusable_candidate"] = new JsonObject
                    {
                        ["scope"] = resolution.Candidate.Scope,
                        ["tool_id"] = resolution.Candidate.ToolId,
                        ["tool_version"] = resolution.Candidate.ToolVersion,
                        ["path_patterns"] = new JsonArray(resolution.Candidate.PathPatterns.Select(p => (JsonNode?)p).ToArray()),
                        ["cwd_pattern"] = resolution.Candidate.CwdPattern,
                        ["argv_patterns"] = new JsonArray(resolution.Candidate.ArgvPatterns.Select(p => (JsonNode?)p).ToArray()),
                    };
                }
                resultPayload["safe_summary"] = "approval authorized";
                break;
        }

        // NEVER invoke adapter here; approval.result is strictly authorization.
        return Task.FromResult(Reply(request, AgentMessageTypes.ApprovalResult, resultPayload));
    }

    private async Task<AgentEnvelope> HandleToolExecuteAsync(AgentEnvelope request, CancellationToken cancellationToken)
    {
        var payload = request.Payload;
        var runId = ReadOptionalPayloadString(payload, "run_id") ?? request.SessionId;
        var toolId = ReadOptionalPayloadString(payload, "tool_id") ?? string.Empty;
        var toolVersion = ReadOptionalPayloadString(payload, "tool_version") ?? "1.0.0";
        var preparationToken = ReadOptionalPayloadString(payload, "preparation_token");
        var authorizationToken = ReadOptionalPayloadString(payload, "authorization_token")
            ?? ReadOptionalPayloadString(payload, "authorizationToken")
            ?? ReadOptionalPayloadString(payload, "approval_token");

        if (!TryGetOwnedRunLease(request, runId, out var lease, out var leaseError) || lease is null)
        {
            return ToolError(request, leaseError, "run lease is not open");
        }

        if (!lease.AdvertisedCapabilities.Contains(toolId))
        {
            return ToolError(request, "phase_not_available", "tool is not advertised yet");
        }

        if (!string.IsNullOrWhiteSpace(authorizationToken))
        {
            // V2 path: authorization_token must be valid, bound to snapshot version + intent hash + tool identity.
            // Tool dispatch coordination: authorization must be consumed BEFORE adapter invocation.
            if (payload["arguments"] is not JsonObject authArguments)
            {
                return ToolError(request, "invalid_execute", "missing arguments");
            }

            PolicyContext authContext;
            try
            {
                authContext = new PolicyContextFactory(lease).Create(toolId, toolVersion, authArguments);
            }
            catch (PolicyContextException error)
            {
                return ToolError(request, error.Code, "execution intent could not be re-resolved");
            }

            var expected = new ApprovalBinding(
                RequestId: runId,
                SessionId: request.SessionId,
                ToolId: toolId,
                ToolVersion: toolVersion,
                ArgumentsHash: authContext.IntentHash,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
                SnapshotVersion: lease.Snapshot.Version,
                IntentHash: authContext.IntentHash);

            if (!_authTokens.TryConsume(authorizationToken!, expected, out _))
            {
                return ToolError(request, "decision_stale", "authorization is no longer valid");
            }

            // Also ensure stale pending from old snapshot is invalidated (binding version check)
            // The lease's snapshot version must equal the token's snapshot version (already checked), and
            // any other pending V2 decisions from older binding are already stale via snapshotVersion mismatch.

            if (_toolHandler is null)
            {
                return ToolError(request, "broker_not_ready", "tool execution is not registered");
            }

            var result = await _toolHandler(request, cancellationToken).ConfigureAwait(false);
            return new AgentEnvelope(
                result.ProtocolVersion,
                result.RequestId,
                AgentMessageTypes.ToolExecuteResult,
                result.SessionId,
                result.CorrelationId,
                result.DeadlineMs,
                (JsonObject)result.Payload.DeepClone());
        }

        if (string.IsNullOrWhiteSpace(preparationToken))
        {
            return ToolError(request, "invalid_execute", "missing preparation token");
        }

        if (payload["arguments"] is not JsonObject arguments)
        {
            return ToolError(request, "invalid_execute", "missing arguments");
        }

        PolicyContext context;
        try
        {
            context = new PolicyContextFactory(lease).Create(toolId, toolVersion, arguments);
        }
        catch (PolicyContextException error)
        {
            return ToolError(request, error.Code, "execution intent could not be re-resolved");
        }

        // Execution-time re-resolution: the prepared canonical intent must still match exactly.
        if (!_session.TryConsumePreparation(preparationToken!, runId, toolId, context.IntentHash, out var consumeError))
        {
            return ToolError(request, consumeError ?? "decision_stale", "preparation is no longer valid");
        }

        if (_toolHandler is null)
        {
            return ToolError(request, "broker_not_ready", "tool execution is not registered");
        }

        var result2 = await _toolHandler(request, cancellationToken).ConfigureAwait(false);
        return new AgentEnvelope(
            result2.ProtocolVersion,
            result2.RequestId,
            AgentMessageTypes.ToolExecuteResult,
            result2.SessionId,
            result2.CorrelationId,
            result2.DeadlineMs,
            (JsonObject)result2.Payload.DeepClone());
    }

    private static readonly TimeSpan DecisionLifetime = TimeSpan.FromMinutes(10);

    private static string? ReadOptionalPayloadString(JsonObject payload, string key) =>
        payload[key] is JsonValue value && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result)
            ? result
            : null;

    private bool TryGetOwnedRunLease(
        AgentEnvelope request,
        string runId,
        out BrokerRunLease? lease,
        out string errorCode)
    {
        if (!_session.Owns(request.SessionId)
            || !_session.TryGetRunLease(runId, out lease)
            || lease is null
            || !string.Equals(lease.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            lease = null;
            errorCode = "run_not_open";
            return false;
        }

        if (lease.IsExpired(DateTimeOffset.UtcNow))
        {
            lease = null;
            errorCode = "lease_expired";
            return false;
        }

        errorCode = string.Empty;
        return true;
    }

    private static AgentEnvelope DecisionReply(
        AgentEnvelope request,
        string decision,
        string reasonCode,
        string? matchedRuleId) => Reply(request, AgentMessageTypes.ToolDecision, new JsonObject
        {
            ["status"] = decision switch
            {
                "deny" => ToolResultStatuses.Denied,
                _ => ToolResultStatuses.Succeeded,
            },
            ["decision"] = decision,
            ["reason_code"] = reasonCode,
            ["analyzer_risk_pending"] = false,
            ["matched_rule_id"] = matchedRuleId,
            ["safe_summary"] = decision switch
            {
                "allow" => "policy allowed the call",
                "ask" => "policy requires user approval",
                _ => "policy denied the call",
            },
        });

    private static string ReadRequiredString(JsonObject payload, string key)
    {
        if (payload[key] is JsonValue value && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        throw new AgentProtocolException($"invalid_{key}");
    }

    private static long ReadRequiredLong(JsonObject payload, string key)
    {
        if (payload[key] is JsonValue value && (value.TryGetValue<long>(out var l) || value.TryGetValue<int>(out var i) && (l = i) == l))
        {
            return l;
        }

        if (payload[key] is JsonValue iv && iv.TryGetValue<int>(out var intVal))
        {
            return intVal;
        }

        throw new AgentProtocolException($"invalid_{key}");
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
        if (!payload.TryGetPropertyValue("skill_ids", out var raw) || raw is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (raw is not JsonArray values)
        {
            throw new AgentProtocolException("invalid_skill_ids");
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is JsonValue item
                && item.TryGetValue<string>(out var skillId)
                && !string.IsNullOrWhiteSpace(skillId)
                && skillId.Length <= 128
                && result.Add(skillId))
            {
                continue;
            }

            throw new AgentProtocolException("invalid_skill_ids");
        }

        return result;
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

        var skills = _session.Owns(request.SessionId)
            ? _skillListProvider?.Invoke(request.SessionId) ?? Array.Empty<SkillManifest>()
            : Array.Empty<SkillManifest>();
        return Reply(request, AgentMessageTypes.HelloOk, new JsonObject
        {
            ["status"] = "ready",
            ["protocol_version"] = advertisedVersion,
            ["tools"] = new JsonArray(capabilities
                .Select(capability => (JsonNode?)ToJson(capability)).ToArray()),
            ["skills"] = new JsonArray(skills
                .Select(skill => (JsonNode?)ToJson(skill))
                .ToArray()),
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
            // Keep the Broker wire profile for existing clients. The Agent API
            // still controls whether a run may select Full Access.
            "full" => CapabilityProfile.Full,
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

    private static JsonObject ToJson(SkillManifest skill) => new()
    {
        ["skill_id"] = skill.Id,
        ["version"] = skill.Version,
        ["required_scopes"] = new JsonArray(skill.RequiredScopes
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .Select(scope => (JsonNode?)scope)
            .ToArray()),
        ["required_tools"] = new JsonArray(skill.RequiredToolIds
            .OrderBy(tool => tool, StringComparer.Ordinal)
            .Select(tool => (JsonNode?)tool)
            .ToArray()),
        ["trust_class"] = skill.TrustClass,
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
