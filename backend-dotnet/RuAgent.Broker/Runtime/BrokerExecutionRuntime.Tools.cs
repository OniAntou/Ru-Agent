using System.Text.Json.Nodes;
using RuAgent.Broker.Diagnostics;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    /// <summary>
    /// v2 <c>tool.execute</c>: the run registration is taken before the
    /// authorization token is consumed, the gate serializes only the preflight
    /// (lease re-check + one-time token consumption), and the execution itself
    /// runs outside the gate so two executes can overlap.
    /// </summary>
    public async Task<ToolExecutionReply> ExecuteAuthorizedAsync(AgentEnvelope request, CancellationToken cancellationToken)
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
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var preparedRunId = ReadRunIdOrSession(request);
            if (!TryGetOwnedRunLease(request, preparedRunId, out var preparedLease, out var preparedLeaseError) || preparedLease is null)
            {
                return ToolExecutionReply.Error(preparedLeaseError, "run lease is not open");
            }

            if (preparedLease.Lifetime.IsRevoked && !_delegations.ContainsKey(preparedRunId))
            {
                return ToolExecutionReply.Error("run_not_open", "run was cancelled before execution started");
            }

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
        if (!TryGetOwnedRunLease(request, runId, out var lease, out var leaseError) || lease is null)
        {
            return ToolExecutionReply.Error(leaseError, "run lease is not open");
        }

        if (lease.Lifetime.IsRevoked && !_delegations.ContainsKey(runId))
        {
            return ToolExecutionReply.Error("run_not_open", "run was cancelled before execution started");
        }

        var executionToken = registration.Token;
        if (executionToken.IsCancellationRequested)
        {
            var errorCode = _delegations.ContainsKey(runId) ? "subagent_parent_revoked" : "run_not_open";
            return ToolExecutionReply.Error(errorCode, "run was cancelled before execution started");
        }

        using var telemetryActivity = BrokerTelemetry.StartToolActivity();
        try
        {
            var result = PayloadReads.OptionalString(request.Payload, "tool_id") == "run_code"
                ? await ExecuteProgramAsync(request, lease, executionToken).ConfigureAwait(false)
                : await preparation.Handler!(request, executionToken).ConfigureAwait(false);
            BrokerTelemetry.SetToolOutcome(
                telemetryActivity,
                BrokerTelemetry.ReadToolId(request.Payload),
                result);
            return ToolExecutionReply.FromReceipt(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (PayloadReads.OptionalString(request.Payload, "tool_id") == "run_code")
        {
            BrokerDiagnosticLog.ToolHandlerFailed(request, error);
            return ToolExecutionReply.FromReceipt(new ToolResult(request.RequestId, request.SessionId,
                ToolResultStatuses.Failed, new JsonObject { ["error"] = "program_execution_unknown", ["effects_unknown"] = true },
                "program_execution_unknown", "Program interrupted; inspect the workspace before any new mutation.", 0, AdapterInvoked: true));
        }
        catch (Exception error)
        {
            BrokerDiagnosticLog.ToolHandlerFailed(request, error);
            return ToolExecutionReply.Error("tool_handler_failed", "tool request failed");
        }
    }

    /// <summary>
    /// v2 <c>tool.evaluate</c>: lease check, advertised-capability check, policy
    /// evaluation, then either a 60s authorization token (allow) or a 30-minute
    /// pending approval bound to snapshot + intent hash (ask).
    /// </summary>
    public Task<ToolEvaluateResult> EvaluateAsync(AgentEnvelope request, CancellationToken cancellationToken) =>
        UnderLifecycleAsync(() => EvaluateCore(request), cancellationToken);

    private ToolEvaluateResult EvaluateCore(AgentEnvelope request)
    {
        var payload = request.Payload;
        var runId = PayloadReads.OptionalString(payload, "run_id") ?? request.SessionId;
        var toolId = PayloadReads.OptionalString(payload, "tool_id") ?? string.Empty;
        var toolVersion = PayloadReads.OptionalString(payload, "tool_version") ?? "1.0.0";

        if (!TryGetOwnedRunLease(request, runId, out var lease, out var leaseError) || lease is null)
        {
            return new ToolEvaluateResult(new RuntimeError(leaseError, "run lease is not open"), null);
        }

        if (lease.Lifetime.IsRevoked && !_delegations.ContainsKey(runId))
        {
            return new ToolEvaluateResult(new RuntimeError("run_not_open", "run lease is not open"), null);
        }

        if (!lease.AdvertisedCapabilities.Contains(toolId))
        {
            // A policy rule cannot grant a tool absent from this lease's advertised surface.
            return new ToolEvaluateResult(new RuntimeError("phase_not_available", "tool is not advertised yet"), null);
        }

        if (payload["arguments"] is not JsonObject arguments)
        {
            return new ToolEvaluateResult(null, DecisionReply("deny", "schema_invalid", null));
        }

        PolicyContext context;
        try
        {
            context = new PolicyContextFactory(lease).Create(toolId, toolVersion, arguments);
            context = BindSubagentControlContext(request, lease, context);
            context = BindProgramContext(request, lease, context);
        }
        catch (PolicyContextException error)
        {
            var issue = error.Code == "malformed_call"
                ? ToolArgumentDiagnostics.Find(toolId, toolVersion, System.Text.Json.JsonSerializer.SerializeToElement(arguments)) : null;
            var hint = issue is null ? error.Hint : string.Join(" ", new[] { error.Hint, issue.Hint }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            return new ToolEvaluateResult(null, DecisionReply("deny", error.Code, null, hint));
        }

        var evaluation = new PolicySnapshotEvaluator(_session.DirectoryGrants)
            .Evaluate(lease.Snapshot, context, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);

        if (evaluation.Decision != "deny" && context.TerminalResolutionError is { } resolutionError)
            return new ToolEvaluateResult(null, DecisionReply("deny", resolutionError, null));

        var delegation = EvaluateDelegation(lease, context);
        if (delegation is { Decision: "deny" } || delegation is { Decision: "ask" } && evaluation.Decision == "allow")
            evaluation = delegation;

        // Users only observe children. Approval-required work returns to the main agent,
        // which can perform its own policy-checked action; never issue a child approval.
        if (delegation is not null && evaluation.Decision == "ask")
            evaluation = new PolicyEvaluation("deny", "subagent_parent_action_required", null);
        if (evaluation.Decision == "ask" && payload["program_nested"]?.GetValue<bool>() == true)
            evaluation = new PolicyEvaluation("deny", "program_requires_direct_call", null);
        // Computer use belongs to the root run's direct calls only (ADR 0031): a child or a
        // program never controls the desktop, even where its policy would allow the action.
        if (string.Equals(toolId, "computer", StringComparison.Ordinal)
            && (delegation is not null || _delegations.ContainsKey(runId) || payload["program_nested"]?.GetValue<bool>() == true))
            evaluation = new PolicyEvaluation("deny", "computer_root_run_required", null);

        string? authorizationToken = null;
        string? approvalId = null;
        DateTimeOffset? approvalExpiresAt = null;
        JsonObject? matcherPreview = null;
        string? approvalKind = null;
        string? boundaryDirectory = null;
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
                    intentHash: context.IntentHash,
                    snapshotSha256: lease.Snapshot.Sha256);
            }
            catch (ArgumentOutOfRangeException)
            {
                return new ToolEvaluateResult(new RuntimeError("run_not_open", "run lease is not open"), null);
            }
        }
        else if (string.Equals(evaluation.Decision, "ask", StringComparison.Ordinal))
        {
            // V2 two-phase approval: persist pending bound to snapshot + intent hash (30-min expiry, exact-by-default preview)
            var toolCallId = PayloadReads.OptionalString(payload, "tool_call_id") ?? request.RequestId;
            var snapshotVersion = lease.Snapshot.Version;
            approvalKind = string.Equals(evaluation.ReasonCode, "external_directory_required", StringComparison.Ordinal)
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
                BoundaryDirectory: approvalKind == "external_directory" ? DeriveGrantDirectory(context) : null,
                SnapshotSha256: lease.Snapshot.Sha256);
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
            approvalId = _pendingV2.IssueV2(binding, expiresAt, context, evaluation.ReasonCode);
            approvalExpiresAt = expiresAt;
            matcherPreview = PendingApprovalStore.BuildMatcherPreview(binding);
            matcherPreview["safe_summary"] = "approval required for " + toolId;
            if (context.PatchTargets.Count > 0)
            {
                matcherPreview["paths"] = new JsonArray(context.PatchTargets
                    .Select(target => (JsonNode?)target.RelativePathInsideWorkspace).ToArray());
                matcherPreview["file_count"] = context.PatchTargets.Count;
                matcherPreview["safe_summary"] = "approval required for all patch targets";
            }
            boundaryDirectory = DeriveGrantDirectory(context);
        }

        var safeSummary = evaluation.Decision switch
        {
            "allow" => "policy allowed the call",
            "ask" => "policy requires user approval",
            _ => "policy denied the call",
        };
        // The ask alone does not tell the model what the user can do about
        // it. Naming the directory plus the session grant turns a stall into
        // one question with a durable answer. Rules stay untouched: this is
        // copy only, the decision above already happened.
        if (approvalId is not null
            && boundaryDirectory is not null
            && string.Equals(evaluation.ReasonCode, "external_directory_required", StringComparison.Ordinal))
        {
            safeSummary = $"approval required for {toolId} outside the workspace ({boundaryDirectory}); the user can grant this directory for the session";
        }

        return new ToolEvaluateResult(
            null,
            new ToolDecisionResult(
                Status: evaluation.Decision switch
                {
                    "allow" => ToolResultStatuses.Succeeded,
                    "deny" => ToolResultStatuses.Denied,
                    _ => ToolResultStatuses.Succeeded,
                },
                Decision: evaluation.Decision,
                ReasonCode: evaluation.ReasonCode,
                AnalyzerRiskPending: evaluation.AnalyzerRiskPending,
                SafeSummary: safeSummary,
                MatchedRuleId: evaluation.MatchedRuleId,
                MatchedRuleIdExplicit: false,
                AuthorizationToken: authorizationToken,
                ApprovalId: approvalId,
                ApprovalKind: approvalKind,
                BoundaryDirectory: boundaryDirectory,
                ExpiresAt: approvalExpiresAt?.ToString("O"),
                MatcherPreview: matcherPreview));
    }

    private ToolExecutePreparation PrepareToolExecute(AgentEnvelope request)
    {
        var payload = request.Payload;
        var runId = PayloadReads.OptionalString(payload, "run_id") ?? request.SessionId;
        var toolId = PayloadReads.OptionalString(payload, "tool_id") ?? string.Empty;
        var toolVersion = PayloadReads.OptionalString(payload, "tool_version") ?? "1.0.0";
        var authorizationToken = PayloadReads.OptionalString(payload, "authorization_token")
            ?? PayloadReads.OptionalString(payload, "authorizationToken")
            ?? PayloadReads.OptionalString(payload, "approval_token");

        if (!TryGetOwnedRunLease(request, runId, out var lease, out var leaseError) || lease is null)
        {
            return new ToolExecutePreparation(
                ToolExecutionReply.Error(leaseError, "run lease is not open"),
                null);
        }

        if (!lease.AdvertisedCapabilities.Contains(toolId))
        {
            return new ToolExecutePreparation(
                ToolExecutionReply.Error("phase_not_available", "tool is not advertised yet"),
                null);
        }

        if (string.IsNullOrWhiteSpace(authorizationToken))
        {
            return new ToolExecutePreparation(
                ToolExecutionReply.Error("invalid_execute", "missing authorization token"),
                null);
        }

        if (payload["arguments"] is not JsonObject authArguments)
        {
            return new ToolExecutePreparation(
                ToolExecutionReply.Error("invalid_execute", "missing arguments"),
                null);
        }

        PolicyContext authContext;
        try
        {
            authContext = new PolicyContextFactory(lease).Create(toolId, toolVersion, authArguments);
            authContext = BindSubagentControlContext(request, lease, authContext);
            authContext = BindProgramContext(request, lease, authContext);
        }
        catch (PolicyContextException error)
        {
            return new ToolExecutePreparation(
                ToolExecutionReply.Error(
                    error.Code,
                    error.Hint ?? "execution intent could not be re-resolved"),
                null);
        }

        if (EvaluateDelegation(lease, authContext) is { Decision: "deny" } delegatedDenial)
            return new ToolExecutePreparation(ToolExecutionReply.Error(delegatedDenial.ReasonCode, "delegation denied execution"), null);

        if (authContext.TerminalResolutionError is not null)
            return new ToolExecutePreparation(ToolExecutionReply.Error("decision_stale", "terminal target is no longer available"), null);

        var expected = new ApprovalBinding(
            RequestId: runId,
            SessionId: request.SessionId,
            ToolId: toolId,
            ToolVersion: toolVersion,
            ArgumentsHash: authContext.IntentHash,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            SnapshotVersion: lease.Snapshot.Version,
            IntentHash: authContext.IntentHash,
            SnapshotSha256: lease.Snapshot.Sha256);

        if (!_authTokens.TryConsume(authorizationToken!, expected, out _))
        {
            return new ToolExecutePreparation(
                ToolExecutionReply.Error("decision_stale", "authorization is no longer valid"),
                null);
        }

        return _authenticatedToolHandler is null
            ? new ToolExecutePreparation(
                ToolExecutionReply.Error("broker_not_ready", "tool execution is not registered"),
                null)
            : new ToolExecutePreparation(null,
                _authenticatedTerminalHandler is { } terminalHandler && authContext.TerminalIntent is { } intent
                    ? (envelope, ct) => terminalHandler(envelope, intent, ct)
                    : _authenticatedToolHandler);
    }

    /// <summary>
    /// Pre-decision refusals: the wire payload always carries
    /// <c>matched_rule_id</c> (even as null) and never a token or approval.
    /// </summary>
    private static ToolDecisionResult DecisionReply(
        string decision,
        string reasonCode,
        string? matchedRuleId,
        string? summary = null) =>
        new(
            Status: decision == "deny" ? ToolResultStatuses.Denied : ToolResultStatuses.Succeeded,
            Decision: decision,
            ReasonCode: reasonCode,
            AnalyzerRiskPending: false,
            SafeSummary: summary ?? decision switch
            {
                "allow" => "policy allowed the call",
                "ask" => "policy requires user approval",
                _ => "policy denied the call",
            },
            MatchedRuleId: matchedRuleId,
            MatchedRuleIdExplicit: true);

    /// <summary>
    /// Nearest existing ancestor directory of the evaluation boundary.
    /// File targets grant their parent; external cwds grant themselves.
    /// Returns null when nothing usable exists (fail closed, no grant).
    /// </summary>
    private static string? DeriveGrantDirectory(PolicyContext context)
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

    private sealed record ToolExecutePreparation(
        ToolExecutionReply? Error,
        Func<AgentEnvelope, CancellationToken, Task<ToolResult>>? Handler);
}
