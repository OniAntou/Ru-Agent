using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
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

    public AgentConnection(
        BrokerSession session,
        Func<CapabilityProfile, IReadOnlyList<AgentToolCapability>> capabilityProvider,
        IReadOnlySet<string> executableScopes,
        Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? toolHandler = null,
        Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? approvalHandler = null,
        Action<string>? sessionClosedHandler = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _capabilityProvider = capabilityProvider ?? throw new ArgumentNullException(nameof(capabilityProvider));
        _executableScopes = new HashSet<string>(
            executableScopes ?? throw new ArgumentNullException(nameof(executableScopes)),
            StringComparer.Ordinal);
        _toolHandler = toolHandler;
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
                if (!_session.Owns(request.SessionId))
                {
                    return ToolError(request, "broker_not_ready", "session is not open");
                }

                return ToolError(request, "skills_disabled", "skills are disabled");
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

                // v2 approval decisions arrive in phase 5; the wire message parses and answers
                // with an explicit approval.result rejection until then.
                return Reply(request, AgentMessageTypes.ApprovalResult, new JsonObject
                {
                    ["status"] = ToolResultStatuses.Denied,
                    ["error_code"] = "phase_not_available",
                    ["safe_summary"] = "v2 approval decisions are not wired yet",
                });
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
        var skillIds = ReadSkillIds(request.Payload);
        if (skillIds.Count > 0)
        {
            return ToolError(request, "skills_disabled", "skills are disabled");
        }

        var scopes = ReadScopes(request.Payload);
        if (scopes.Any(scope => !_executableScopes.Contains(scope)))
        {
            return ToolError(request, "scope_unavailable", "scope is not executable");
        }

        if (!_session.Open(request.SessionId, scopes, new HashSet<string>(StringComparer.Ordinal)))
        {
            return ToolError(request, "session_conflict", "session is already owned");
        }

        return Reply(request, AgentMessageTypes.SessionOpened, new JsonObject
        {
            ["status"] = ToolResultStatuses.Succeeded,
            ["scopes"] = new JsonArray(_session.Scopes
                .OrderBy(scope => scope, StringComparer.Ordinal)
                .Select(scope => (JsonNode?)scope).ToArray()),
            ["skill_ids"] = new JsonArray(),
            ["safe_summary"] = "session opened",
        });
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

    private AgentEnvelope HandleRunOpen(AgentEnvelope request)
    {
        try
        {
            var lease = ReadRunLease(request);
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
            if (!_session.TryRebindRunPolicy(lease, out var errorCode))
            {
                return ToolError(request, errorCode ?? "rebind_failed", "run policy rebind failed");
            }

            var bound = _session.ActiveRunLease!;
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
        if (!DateTimeOffset.TryParse(expiresAtStr, out var expiresAt))
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
            snapshot);
    }

    private AgentEnvelope HandleRunClose(AgentEnvelope request)
    {
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

        if (!_session.TryGetRunLease(runId, out var lease) || lease is null)
        {
            return Task.FromResult(ToolError(request, "run_not_open", "run lease is not open"));
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
        else if (string.Equals(evaluation.Decision, "ask", StringComparison.Ordinal))
        {
            // Approvals over v2 are wired in phase 5; an ask never dispatches before that.
            replyPayload["error_code"] = "approval_not_wired";
        }

        return Task.FromResult(Reply(request, AgentMessageTypes.ToolDecision, replyPayload));
    }

    private Task<AgentEnvelope> HandleToolPrepareAsync(AgentEnvelope request, CancellationToken cancellationToken)
    {
        var payload = request.Payload;
        var runId = ReadOptionalPayloadString(payload, "run_id") ?? request.SessionId;
        var toolId = ReadOptionalPayloadString(payload, "tool_id") ?? string.Empty;
        var decisionToken = ReadOptionalPayloadString(payload, "decision_token");

        if (!_session.TryGetRunLease(runId, out _) )
        {
            return Task.FromResult(ToolError(request, "run_not_open", "run lease is not open"));
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

    private async Task<AgentEnvelope> HandleToolExecuteAsync(AgentEnvelope request, CancellationToken cancellationToken)
    {
        var payload = request.Payload;
        var runId = ReadOptionalPayloadString(payload, "run_id") ?? request.SessionId;
        var toolId = ReadOptionalPayloadString(payload, "tool_id") ?? string.Empty;
        var toolVersion = ReadOptionalPayloadString(payload, "tool_version") ?? "1.0.0";
        var preparationToken = ReadOptionalPayloadString(payload, "preparation_token");

        if (!_session.TryGetRunLease(runId, out var lease) || lease is null)
        {
            return ToolError(request, "run_not_open", "run lease is not open");
        }

        if (!lease.AdvertisedCapabilities.Contains(toolId))
        {
            return ToolError(request, "phase_not_available", "tool is not advertised yet");
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

    private static readonly TimeSpan DecisionLifetime = TimeSpan.FromMinutes(10);

    private static string? ReadOptionalPayloadString(JsonObject payload, string key) =>
        payload[key] is JsonValue value && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result)
            ? result
            : null;

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
            ["skills"] = new JsonArray(),
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

    private static AgentEnvelope Reply(AgentEnvelope request, string type, JsonObject payload)
        => new(
            request.ProtocolVersion,
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
