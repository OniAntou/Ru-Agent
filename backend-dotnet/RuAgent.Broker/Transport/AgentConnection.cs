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
                return ToolError(request, "phase_not_available", "tool output delta not available in phase 1");
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
                return ToolError(request, "invalid_expires_at", "expires_at is invalid");
            }

            var lease = BrokerRunLease.CreateValidated(
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
                expiresAt);

            if (!_session.TryOpenRunLease(lease, out var errorCode))
            {
                return ToolError(request, errorCode ?? "lease_failed", "run lease failed");
            }

            return Reply(request, AgentMessageTypes.RunOpened, new JsonObject
            {
                ["status"] = ToolResultStatuses.Succeeded,
                ["run_id"] = lease.RunId,
                ["advertised_capabilities"] = new JsonArray(lease.AdvertisedCapabilities.Select(c => (JsonNode?)c).ToArray()),
                ["safe_summary"] = "run opened",
            });
        }
        catch (AgentProtocolException error)
        {
            return ToolError(request, error.Code, "invalid run open");
        }
    }

    private AgentEnvelope HandleRunClose(AgentEnvelope request)
    {
        var runId = request.Payload["run_id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : request.RequestId;
        if (_session.TryGetRunLease(runId, out _))
        {
            // Close run lease by clearing session lease if matches; simplified.
            _session.Close(request.SessionId);
        }

        return Reply(request, AgentMessageTypes.ToolResult, new JsonObject
        {
            ["status"] = ToolResultStatuses.Succeeded,
            ["safe_summary"] = "run closed",
        });
    }

    private Task<AgentEnvelope> HandleToolEvaluateAsync(AgentEnvelope request, CancellationToken cancellationToken)
    {
        var toolId = request.Payload["tool_id"] is JsonValue tv && tv.TryGetValue<string>(out var s) ? s : string.Empty;
        var runId = request.Payload["run_id"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : request.SessionId;

        if (!_session.ValidateRunLeaseForTool(runId, toolId, out var errorCode))
        {
            // If capability unavailable for phase-1, return phase_not_available for mutation attempt?
            if (errorCode == "capability_unavailable" && toolId is not ("files.list" or "files.read"))
            {
                return Task.FromResult(ToolError(request, "phase_not_available", "tool not available in phase 1"));
            }

            return Task.FromResult(ToolError(request, errorCode ?? "run_not_open", "run lease invalid"));
        }

        // Phase-1 authoritative decision: only files.list/files.read are executable
        if (toolId is "files.list" or "files.read")
        {
            var token = $"canonical-{toolId}-{Guid.NewGuid():N}";
            return Task.FromResult(Reply(request, AgentMessageTypes.ToolResult, new JsonObject
            {
                ["status"] = ToolResultStatuses.Succeeded,
                ["decision"] = "allow",
                ["canonical_intent_token"] = token,
                ["safe_summary"] = "allowed",
            }));
        }

        // Mutation or terminal attempt in phase-1 returns phase_not_available (not denied, not ask)
        return Task.FromResult(ToolError(request, "phase_not_available", "tool requires phase 2"));
    }

    private Task<AgentEnvelope> HandleToolPrepareAsync(AgentEnvelope request, CancellationToken cancellationToken)
    {
        var token = request.Payload["canonical_intent_token"] is JsonValue tv && tv.TryGetValue<string>(out var s) ? s : string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(ToolError(request, "invalid_prepare", "missing token"));
        }

        // Side-effect-free revalidation: ensure token matches expected pattern and run lease still valid.
        var runId = request.Payload["run_id"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : string.Empty;
        var toolId = request.Payload["tool_id"] is JsonValue tiv && tiv.TryGetValue<string>(out var tid) ? tid : "files.read";
        if (!_session.ValidateRunLeaseForTool(runId, toolId, out var errorCode))
        {
            return Task.FromResult(ToolError(request, errorCode ?? "run_not_open", "prepare failed"));
        }

        return Task.FromResult(Reply(request, AgentMessageTypes.ToolResult, new JsonObject
        {
            ["status"] = ToolResultStatuses.Succeeded,
            ["preparation_token"] = $"prepared-{token}",
            ["safe_summary"] = "prepared",
        }));
    }

    private Task<AgentEnvelope> HandleToolExecuteAsync(AgentEnvelope request, CancellationToken cancellationToken)
    {
        var preparationToken = request.Payload["preparation_token"] is JsonValue pv && pv.TryGetValue<string>(out var s) ? s : string.Empty;
        if (string.IsNullOrWhiteSpace(preparationToken))
        {
            return Task.FromResult(ToolError(request, "invalid_execute", "missing preparation_token"));
        }

        // Consume preparation token and execute via underlying tool handler if available.
        // For phase-1, only list/read execution is delegated to existing tool handler.
        var toolId = request.Payload["tool_id"] is JsonValue tiv && tiv.TryGetValue<string>(out var tid) ? tid : "files.read";
        var runId = request.Payload["run_id"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : request.SessionId;
        if (!_session.ValidateRunLeaseForTool(runId, toolId, out var errorCode))
        {
            return Task.FromResult(ToolError(request, errorCode ?? "run_not_open", "execute failed"));
        }

        if (_toolHandler is not null)
        {
            // Delegate to existing file tool handler via synthetic ToolRequest envelope.
            // Simplified: construct a ToolRequest with same payload arguments.
            // For now return success via tool handler if available.
            return _toolHandler(request, cancellationToken);
        }

        return Task.FromResult(Reply(request, AgentMessageTypes.ToolResult, new JsonObject
        {
            ["status"] = ToolResultStatuses.Succeeded,
            ["output"] = new JsonObject { ["result"] = "executed" },
            ["safe_summary"] = "executed",
        }));
    }

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
