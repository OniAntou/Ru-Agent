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
            return Task.FromResult(DecisionReply(request, "deny", error.Code, null, error.Hint));
        }

        var evaluation = new PolicySnapshotEvaluator(_session.DirectoryGrants)
            .Evaluate(lease.Snapshot, context, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);

        string? authorizationToken = null;
        string? approvalId = null;
        DateTimeOffset? approvalExpiresAt = null;
        JsonObject? matcherPreview = null;
        if (string.Equals(evaluation.Decision, "allow", StringComparison.Ordinal))
        {
            try
            {
                authorizationToken = _authTokens.Issue(
                    requestId: runId,
                    sessionId: request.SessionId,
                    toolId: toolId,
                    toolVersion: toolVersion,
                    argumentsHash: context.IntentHash,
                    lifetime: TimeSpan.FromSeconds(60),
                    snapshotVersion: lease.Snapshot.Version,
                    intentHash: context.IntentHash);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Task.FromResult(ToolError(request, "run_not_open", "run lease is not open"));
            }
        }
        else if (string.Equals(evaluation.Decision, "ask", StringComparison.Ordinal))
        {
            // V2 two-phase approval: persist pending bound to snapshot + intent hash (30-min expiry, exact-by-default preview)
            var toolCallId = ReadOptionalPayloadString(payload, "tool_call_id") ?? request.RequestId;
            var snapshotVersion = lease.Snapshot.Version;
            var approvalKind = string.Equals(evaluation.ReasonCode, "external_directory_required", StringComparison.Ordinal)
                ? "external_directory"
                : "terminal_risk";
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
                IntentHash: context.IntentHash,
                ApprovalKind: approvalKind,
                BoundaryDirectory: approvalKind == "external_directory" ? DeriveGrantDirectory(context) : null);
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

        if (authorizationToken is not null)
        {
            replyPayload["authorization_token"] = authorizationToken;
        }

        if (approvalId is not null)
        {
            replyPayload["approval_id"] = approvalId;
            replyPayload["approval_kind"] = string.Equals(evaluation.ReasonCode, "external_directory_required", StringComparison.Ordinal)
                ? "external_directory"
                : "terminal_risk";
            var grantDirectory = DeriveGrantDirectory(context);
            if (grantDirectory is not null)
            {
                replyPayload["boundary_directory"] = grantDirectory;
            }

            replyPayload["expires_at"] = approvalExpiresAt!.Value.ToString("O");
            replyPayload["matcher_preview"] = matcherPreview!.DeepClone();
        }

        return Task.FromResult(Reply(request, AgentMessageTypes.ToolDecision, replyPayload));
    }

    private async Task<AgentEnvelope> HandleToolExecuteAsync(AgentEnvelope request, CancellationToken cancellationToken)
    {
        // Registered before the authorization token is consumed, so a run close
        // or an explicit cancel that lands between the lease check and the handler
        // start still reaches the runner; registering afterwards left a window in
        // which the token was already spent but the execution was untracked.
        using var registration = _runCancellations.Register(
            ReadRunIdOrSession(request),
            cancellationToken);

        ToolExecutePreparation preparation;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            preparation = PrepareToolExecute(request);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (preparation.Error is not null)
        {
            return preparation.Error;
        }

        // The registration may already have been signalled (a cancel or a run
        // close raced with the preparation): the token is one-time, so check the
        // lease again before invoking the handler.
        var runId = ReadRunIdOrSession(request);
        if (!TryGetOwnedRunLease(request, runId, out _, out var leaseError))
        {
            return ToolError(request, leaseError, "run lease is not open");
        }

        var executionToken = registration.Token;
        if (executionToken.IsCancellationRequested)
        {
            return ToolError(request, "run_not_open", "run was cancelled before execution started");
        }

        var result = _authenticatedToolHandler is { } authenticatedHandler
            ? await authenticatedHandler(request, executionToken).ConfigureAwait(false)
            : await preparation.Handler!(request, executionToken).ConfigureAwait(false);
        return new AgentEnvelope(
            result.ProtocolVersion,
            result.RequestId,
            AgentMessageTypes.ToolExecuteResult,
            result.SessionId,
            result.CorrelationId,
            result.DeadlineMs,
            (JsonObject)result.Payload.DeepClone());
    }

    private ToolExecutePreparation PrepareToolExecute(AgentEnvelope request)
    {
        var payload = request.Payload;
        var runId = ReadOptionalPayloadString(payload, "run_id") ?? request.SessionId;
        var toolId = ReadOptionalPayloadString(payload, "tool_id") ?? string.Empty;
        var toolVersion = ReadOptionalPayloadString(payload, "tool_version") ?? "1.0.0";
        var authorizationToken = ReadOptionalPayloadString(payload, "authorization_token")
            ?? ReadOptionalPayloadString(payload, "authorizationToken")
            ?? ReadOptionalPayloadString(payload, "approval_token");

        if (!TryGetOwnedRunLease(request, runId, out var lease, out var leaseError) || lease is null)
        {
            return new ToolExecutePreparation(
                ToolError(request, leaseError, "run lease is not open"),
                null);
        }

        if (!lease.AdvertisedCapabilities.Contains(toolId))
        {
            return new ToolExecutePreparation(
                ToolError(request, "phase_not_available", "tool is not advertised yet"),
                null);
        }

        if (string.IsNullOrWhiteSpace(authorizationToken))
        {
            return new ToolExecutePreparation(
                ToolError(request, "invalid_execute", "missing authorization token"),
                null);
        }

        if (payload["arguments"] is not JsonObject authArguments)
        {
            return new ToolExecutePreparation(
                ToolError(request, "invalid_execute", "missing arguments"),
                null);
        }

        PolicyContext authContext;
        try
        {
            authContext = new PolicyContextFactory(lease).Create(toolId, toolVersion, authArguments);
        }
        catch (PolicyContextException error)
        {
            return new ToolExecutePreparation(
                ToolError(
                    request,
                    error.Code,
                    error.Hint ?? "execution intent could not be re-resolved"),
                null);
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
            return new ToolExecutePreparation(
                ToolError(request, "decision_stale", "authorization is no longer valid"),
                null);
        }

        return _toolHandler is null && _authenticatedToolHandler is null
            ? new ToolExecutePreparation(
                ToolError(request, "broker_not_ready", "tool execution is not registered"),
                null)
            : new ToolExecutePreparation(null, _toolHandler);
    }

    private sealed record ToolExecutePreparation(
        AgentEnvelope? Error,
        Func<AgentEnvelope, CancellationToken, Task<AgentEnvelope>>? Handler);

    private static string? ReadOptionalPayloadString(JsonObject payload, string key) =>
        payload[key] is JsonValue value && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result)
            ? result
            : null;

    /// <summary>
    /// The run a cancel applies to. A connection owns one lease per run, so the
    /// session id is only the fallback for envelopes that omit run_id.
    /// </summary>
    private static string ReadRunIdOrSession(AgentEnvelope request) =>
        ReadOptionalPayloadString(request.Payload, "run_id") ?? request.SessionId;

    /// <summary>
    /// Nearest existing ancestor directory of the evaluation boundary.
    /// File targets grant their parent; external cwds grant themselves.
    /// Returns null when nothing usable exists (fail closed, no grant).
    /// </summary>
    private static string? DeriveGrantDirectory(Policy.PolicyContext context)
    {
        var raw = context.Target?.CanonicalPath
            ?? context.WorkingDirectory?.CanonicalPath
            ?? context.CanonicalCwd;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 4096)
        {
            return null;
        }

        try
        {
            var current = Directory.Exists(raw)
                ? Path.GetFullPath(raw)
                : Path.GetDirectoryName(Path.GetFullPath(raw));
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            if (string.IsNullOrEmpty(current) || current.Length > 4096)
            {
                return null;
            }

            return current;
        }
        catch
        {
            return null;
        }
    }

    private AgentEnvelope HandleSessionGrantsRevoke(AgentEnvelope request)
    {
        if (!_session.Owns(request.SessionId))
        {
            return ToolError(request, "broker_not_ready", "session is not open");
        }

        var targetSession = ReadOptionalPayloadString(request.Payload, "agent_session_id");
        if (string.IsNullOrWhiteSpace(targetSession) || targetSession.Length > 128)
        {
            return Reply(request, AgentMessageTypes.SessionGrantsRevoked, new JsonObject
            {
                ["status"] = ToolResultStatuses.Denied,
                ["error_code"] = "invalid_session_id",
                ["safe_summary"] = "invalid agent session id",
            });
        }

        _session.DirectoryGrants.RevokeSession(targetSession);
        return Reply(request, AgentMessageTypes.SessionGrantsRevoked, new JsonObject
        {
            ["status"] = ToolResultStatuses.Succeeded,
            ["safe_summary"] = "session grants revoked",
        });
    }

    private static AgentEnvelope DecisionReply(
        AgentEnvelope request,
        string decision,
        string reasonCode,
        string? matchedRuleId,
        string? summary = null) => Reply(request, AgentMessageTypes.ToolDecision, new JsonObject
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
            // A repair hint for malformed calls travels as the bounded summary so
            // the model can fix the argument shape instead of repeating the call.
            ["safe_summary"] = summary ?? decision switch
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
        => Reply(
            request,
            request.MessageType == AgentMessageTypes.ToolExecute ? AgentMessageTypes.ToolExecuteResult : AgentMessageTypes.ToolResult,
            new JsonObject
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
}
