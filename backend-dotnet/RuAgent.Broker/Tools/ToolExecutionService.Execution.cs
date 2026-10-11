using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    public Task<ToolResult> ExecuteAsync(
        string sessionId,
        ToolManifest manifest,
        JsonObject arguments,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(arguments);
        return ExecuteAsync(
            new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersion,
                $"skill-tool-{Guid.NewGuid():N}",
                AgentMessageTypes.ToolRequest,
                sessionId,
                null,
                deadlineMs,
                new JsonObject
                {
                    ["tool_id"] = manifest.Id,
                    ["tool_version"] = manifest.Version,
                    ["arguments"] = arguments.DeepClone(),
                    ["capability_class"] = "internal",
                }),
            cancellationToken);
    }

    public Task<ToolResult> ExecuteAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(request, cancellationToken, isAuthenticatedV2Execution: false);

    internal Task<ToolResult> ExecuteAuthenticatedV2Async(
        AgentEnvelope request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(request, cancellationToken, isAuthenticatedV2Execution: true);

    private async Task<ToolResult> ExecuteAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken,
        bool isAuthenticatedV2Execution)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var toolId = ReadString(request.Payload, "tool_id");
            var toolVersion = ReadString(request.Payload, "tool_version");
            var arguments = request.Payload["arguments"] is JsonObject objectNode
                ? objectNode
                : throw new ToolExecutionException("invalid_arguments");
            var binding = _catalog.Registry.Get(toolId, toolVersion);
            var decision = EvaluateWithOpScope(request.SessionId, binding.Manifest, arguments);
            if (!decision.Allowed)
            {
                return Result(
                    request,
                    ToolResultStatuses.Denied,
                    new JsonObject(),
                    decision.Code,
                    "Tool scope is not granted.",
                    started,
                    ToolVerificationStates.NotRequired,
                    adapterInvoked: false);
            }

            try
            {
                ToolSchemaValidator.Validate(binding.Manifest.InputSchema, arguments);
                ValidateTerminalTimeoutCeiling(toolId, arguments);
            }
            catch (ToolSchemaException error)
            {
                return Result(
                    request,
                    ToolResultStatuses.Failed,
                    new JsonObject(),
                    error.Code,
                    "Tool arguments were rejected.",
                    started,
                    ToolVerificationStates.NotRequired,
                    adapterInvoked: false);
            }

            var isAuthenticatedV2 = isAuthenticatedV2Execution
                && request.ProtocolVersion == AgentEnvelopeCodec.ProtocolVersionV2
                && request.MessageType == AgentMessageTypes.ToolExecute;
            var isV2Authorized = isAuthenticatedV2;
            if (decision.RequiresApproval && !isV2Authorized)
            {
                var expiresAt = DateTimeOffset.UtcNow.AddSeconds(60);
                var argumentsHash = PendingApprovalStore.ComputeArgumentsHash(toolId, toolVersion, arguments);
                var terminalApprovalToken = toolId is "bash" or "terminal.run" or "terminal.shell"
                    ? _approvalTokens.Issue(
                        request.RequestId,
                        request.SessionId,
                        toolId,
                        toolVersion,
                        TryComputeTerminalIntentHash(_defaultTools, toolId, arguments) ?? argumentsHash,
                        TimeSpan.FromSeconds(60))
                    : null;
                var approvalId = _approvals.Issue(
                    request,
                    binding.Manifest,
                    arguments,
                    SafeAction(binding.Manifest),
                    expiresAt,
                    terminalApprovalToken);
                return Result(
                    request,
                    ToolResultStatuses.ApprovalRequired,
                    new JsonObject
                    {
                        ["approval_id"] = approvalId,
                        ["tool_id"] = binding.Manifest.Id,
                        ["tool_version"] = binding.Manifest.Version,
                        ["scope"] = string.Join(", ", binding.Manifest.Scopes.OrderBy(value => value, StringComparer.Ordinal)),
                        ["safe_action"] = SafeAction(binding.Manifest),
                        ["expires_at"] = expiresAt.ToString("O"),
                        [ApprovalPreviewFields.Field] = BuildPreview(
                            toolId,
                            arguments,
                            ReadTargetFileText(arguments)),
                    },
                    "approval_required",
                    "Approval is required.",
                    started,
                    ToolVerificationStates.Pending,
                    adapterInvoked: false);
            }

            return await ExecuteBindingAsync(
                receiptRequest: request,
                contextRequest: request,
                binding,
                arguments,
                approved: isV2Authorized,
                approvalToken: null,
                ReadOptionalString(request.Payload, "run_id") ?? request.SessionId,
                isAuthenticatedV2,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(
                request,
                ToolResultStatuses.Cancelled,
                new JsonObject(),
                "cancelled",
                "Tool cancelled.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
        catch (KeyNotFoundException)
        {
            return Result(
                request,
                ToolResultStatuses.Failed,
                new JsonObject(),
                "tool_not_found",
                "Tool is not available.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
        catch (ToolExecutionException error)
        {
            return Result(
                request,
                ToolResultStatuses.Failed,
                new JsonObject(),
                error.Code,
                "Tool request was rejected.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
        catch (Exception error)
        {
            LogAdapterFailure(request, error);
            return Result(
                request,
                ToolResultStatuses.Failed,
                new JsonObject(),
                "tool_failed",
                "Tool failed. See the broker diagnostic log for the adapter error.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
    }

    public async Task<ToolResult> RespondToApprovalAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var approvalId = ReadString(request.Payload, "approval_id");
            var approved = ReadBool(request.Payload, "approved");
            if (!_approvals.TryConsume(
                    approvalId,
                    request.SessionId,
                    approved,
                    DateTimeOffset.UtcNow,
                    out var pending))
            {
                return Result(
                    request,
                    ToolResultStatuses.Denied,
                    new JsonObject(),
                    "approval_stale",
                    "Approval is missing, expired, or already used.",
                    started,
                    ToolVerificationStates.NotRequired,
                    adapterInvoked: false);
            }

            if (!pending!.Approved)
            {
                return Result(
                    request,
                    ToolResultStatuses.Denied,
                    new JsonObject(),
                    "approval_rejected",
                    "Approval was rejected.",
                    started,
                    ToolVerificationStates.NotRequired,
                    adapterInvoked: false);
            }

            var binding = _catalog.Registry.Get(pending.Manifest.Id, pending.Manifest.Version);
            var decision = EvaluateWithOpScope(pending.Request.SessionId, binding.Manifest, pending.Arguments);
            if (!decision.Allowed)
            {
                return Result(
                    request,
                    ToolResultStatuses.Denied,
                    new JsonObject(),
                    decision.Code,
                    "Tool scope is no longer granted.",
                    started,
                    ToolVerificationStates.NotRequired,
                    adapterInvoked: false);
            }

            return await ExecuteBindingAsync(
                receiptRequest: request,
                contextRequest: pending.Request,
                binding,
                pending.Arguments,
                approved: pending.TerminalApprovalToken is null,
                pending.TerminalApprovalToken,
                ReadOptionalString(pending.Request.Payload, "run_id") ?? pending.Request.SessionId,
                isAuthenticatedV2Execution: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(
                request,
                ToolResultStatuses.Cancelled,
                new JsonObject(),
                "cancelled",
                "Approved action cancelled.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
        catch (ToolExecutionException error)
        {
            return Result(
                request,
                ToolResultStatuses.Failed,
                new JsonObject(),
                error.Code,
                "Approved action was rejected.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
        catch (Exception)
        {
            return Result(
                request,
                ToolResultStatuses.Failed,
                new JsonObject(),
                "tool_failed",
                "Approved action failed. See the broker diagnostic log for the adapter error.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }
    }

    private ToolPolicyDecision EvaluateWithOpScope(string sessionId, ToolManifest manifest, JsonObject arguments)
    {
        var grant = new PolicyGrant(sessionId, _session.ScopesFor(sessionId), _session.ExpiresAt);
        var decision = _policy.Evaluate(grant, manifest);
        if (!decision.Allowed)
        {
            return decision;
        }

        if (manifest.Id is "read" or "write" or "bash" or "grep" or "glob" or "web_search" or "web_fetch")
        {
            return EvaluateOpGrant(grant, manifest, arguments) ?? decision;
        }

        return decision;
    }

    private ToolPolicyDecision? EvaluateOpGrant(PolicyGrant grant, ToolManifest manifest, JsonObject arguments)
    {
        if (manifest.Id is "web_search" or "web_fetch")
        {
            var scope = manifest.Id == "web_search" ? ToolScope.WebSearch : ToolScope.WebFetch;
            return grant.Scopes.Contains(scope)
                ? new ToolPolicyDecision(true, true, "allowed")
                : new ToolPolicyDecision(false, false, "scope_missing");
        }

        if (manifest.Id is "grep" or "glob")
        {
            var scopes = new HashSet<string>(manifest.Scopes, StringComparer.Ordinal);
            scopes.Remove(ToolScope.WebSearch);
            scopes.Remove(ToolScope.WebFetch);
            return scopes.All(grant.Scopes.Contains)
                ? new ToolPolicyDecision(true, false, "allowed")
                : new ToolPolicyDecision(false, false, "scope_missing");
        }

        var op = ReadOptionalString(arguments, "op");
        if (manifest.Id == "read")
        {
            if (op is "help")
            {
                return new ToolPolicyDecision(true, false, "allowed");
            }

            if (op is "list" or "read")
            {
                var manifestScopes = new HashSet<string>(manifest.Scopes, StringComparer.Ordinal);
                manifestScopes.Remove(ToolScope.WebSearch);
                manifestScopes.Remove(ToolScope.WebFetch);
                if (!manifestScopes.All(grant.Scopes.Contains))
                {
                    return new ToolPolicyDecision(false, false, "scope_missing");
                }

                return new ToolPolicyDecision(true, false, "allowed");
            }

            return null;
        }

        if (manifest.Id == "write")
        {
            if (op is "write" or "delete" or "restore")
            {
                return grant.Scopes.Contains(ToolScope.WorkspaceWrite)
                    ? new ToolPolicyDecision(true, true, "allowed")
                    : new ToolPolicyDecision(false, false, "scope_missing");
            }

            return null;
        }

        if (manifest.Id == "bash")
        {
            return grant.Scopes.Contains(ToolScope.TerminalExecute)
                ? new ToolPolicyDecision(true, true, "allowed")
                : new ToolPolicyDecision(false, false, "scope_missing");
        }

        return null;
    }

    private bool IsWindowsCurrentUserAuthority(
        string runId,
        string sessionId,
        bool isAuthenticatedV2Execution)
    {
        if (!isAuthenticatedV2Execution || ActiveLease(runId, sessionId) is not { } lease)
        {
            return false;
        }

        return AgentPolicyProfileCatalog.Get(lease.Preset).TerminalAuthority
            == PresetTerminalAuthority.WindowsCurrentUser;
    }

    /// <summary>
    /// The schema accepts the background lifetime ceiling so one schema serves
    /// both modes; the foreground hard maximum stays enforced here, before any
    /// adapter work, for callers that reach execution without policy
    /// evaluation.
    /// </summary>
    private static void ValidateTerminalTimeoutCeiling(string toolId, JsonObject arguments)
    {
        if (!IsTerminalRunOrShellCall(toolId, arguments))
        {
            return;
        }

        if (arguments["background"] is JsonValue backgroundValue
            && backgroundValue.TryGetValue<bool>(out var background)
            && background)
        {
            return;
        }

        if (arguments["timeout_ms"] is JsonValue timeoutValue
            && timeoutValue.TryGetValue<int>(out var timeout)
            && timeout > TerminalLimits.HardMaxTimeoutMs)
        {
            throw new ToolSchemaException("invalid_arguments");
        }
    }

    /// <summary>
    /// True for a bash run/shell call. Those executions are bounded by the
    /// terminal timeout and the runner's own kill timer, not by the envelope
    /// deadline, which is a control-RPC budget.
    /// </summary>
    private static bool IsTerminalRunOrShellCall(string toolId, JsonObject arguments)
    {
        if (!string.Equals(toolId, "bash", StringComparison.Ordinal))
        {
            return false;
        }

        var mode = arguments["mode"] is JsonValue modeValue && modeValue.TryGetValue<string>(out var text)
            ? text
            : "run";
        return mode is "run" or "shell";
    }

    private async Task<ToolResult> ExecuteBindingAsync(
        AgentEnvelope receiptRequest,
        AgentEnvelope contextRequest,
        ToolBinding binding,
        JsonObject arguments,
        bool approved,
        string? approvalToken,
        string runId,
        bool isAuthenticatedV2Execution,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            ToolSchemaValidator.Validate(binding.Manifest.InputSchema, arguments);
            ValidateTerminalTimeoutCeiling(ReadString(contextRequest.Payload, "tool_id"), arguments);
        }
        catch (ToolSchemaException error)
        {
            return Result(
                receiptRequest,
                ToolResultStatuses.Failed,
                new JsonObject(),
                error.Code,
                "Tool arguments were rejected.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result(
                receiptRequest,
                ToolResultStatuses.Cancelled,
                new JsonObject(),
                "cancelled",
                "Tool cancelled.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }

        var sessionScopes = _session.ScopesFor(contextRequest.SessionId);
        var context = new ToolExecutionContext(
            contextRequest.RequestId,
            contextRequest.SessionId,
            contextRequest.CorrelationId,
            ReadCapabilityClass(contextRequest.Payload),
            contextRequest.DeadlineMs,
            approved,
            approvalToken,
            ConfinementRequired: !IsWindowsCurrentUserAuthority(
                runId,
                contextRequest.SessionId,
                isAuthenticatedV2Execution),
            sessionScopes,
            runId,
            IsAuthenticatedV2Execution: isAuthenticatedV2Execution);
        ToolExecutionOutcome outcome;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!IsTerminalRunOrShellCall(ReadString(contextRequest.Payload, "tool_id"), arguments))
        {
            deadline.CancelAfter(Math.Clamp(context.DeadlineMs, 1, 300_000));
        }
        try
        {
            outcome = await binding.Executor(context, arguments, deadline.Token).ConfigureAwait(false);
            outcome = Normalize(binding, outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = new ToolExecutionOutcome(
                ToolResultStatuses.Cancelled,
                new JsonObject(),
                "process_cancelled",
                "Tool cancelled.",
                ToolVerificationStates.NotRequired);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            outcome = new ToolExecutionOutcome(
                ToolResultStatuses.TimedOut,
                new JsonObject(),
                "process_timed_out",
                "Tool timed out.",
                ToolVerificationStates.NotRequired);
        }
        catch (ToolSchemaException error)
        {
            outcome = ToolExecutionOutcome.Failed(error.Code, "Tool output was rejected.", VerificationFailure(binding));
        }
        catch (ToolExecutionException error)
        {
            outcome = ToolExecutionOutcome.Failed(error.Code, "Tool arguments were rejected.", VerificationFailure(binding));
        }
        catch (UnauthorizedAccessException error)
        {
            outcome = ToolExecutionOutcome.Failed(
                StableCode(error.Message, "access_denied"),
                "Tool access was denied.",
                VerificationFailure(binding));
        }
        catch (ArgumentException error)
        {
            outcome = ToolExecutionOutcome.Failed(
                StableCode(error.Message, "invalid_arguments"),
                "Tool arguments were rejected.",
                VerificationFailure(binding));
        }
        catch (IOException error)
        {
            outcome = ToolExecutionOutcome.Failed(
                StableCode(error.Message, "io_error"),
                IoRepairHint(error.Message),
                VerificationFailure(binding));
        }
        catch (InvalidDataException error)
        {
            outcome = ToolExecutionOutcome.Failed(
                StableCode(error.Message, "invalid_data"),
                "Tool response data was rejected.",
                VerificationFailure(binding));
        }
        catch (EditMatchException error)
        {
            // An edit that matched zero or an unexpected number of spans keeps
            // its stable code but tells the model how to repair the call. Only
            // the match count travels; no file content leaks into the receipt.
            outcome = ToolExecutionOutcome.Failed(
                error.Code,
                error.MatchCount > 0
                    ? $"old_string matched {error.MatchCount} times. Set expected_occurrences to {error.MatchCount} to replace all, or narrow old_string to a unique span."
                    : "old_string was not found in the file. Re-read the file and copy the exact text including whitespace.",
                VerificationFailure(binding));
        }
        catch (Exception error)
        {
            LogAdapterFailure(receiptRequest, error);
            outcome = ToolExecutionOutcome.Failed(
            "adapter_error",
            "Tool adapter failed. The broker diagnostic log carries the adapter error.",
            VerificationFailure(binding));
        }

        outcome = outcome with
        {
            DurationMs = checked((int)Math.Min(int.MaxValue, started.ElapsedMilliseconds)),
        };
        return Receipt(receiptRequest, outcome, adapterInvoked: true);
    }

    private ToolExecutionOutcome ProcessOutcome(
        StreamingProcessResult result,
        bool confineWorkspace,
        string canonicalRoot)
    {
        var output = new JsonObject
        {
            ["exit_code"] = result.ExitCode,
            ["duration_ms"] = result.DurationMs,
            ["stdout_head"] = result.StdoutHead,
            ["stdout_tail"] = result.StdoutTail,
            ["stderr_head"] = result.StderrHead,
            ["stderr_tail"] = result.StderrTail,
            ["stdout_bytes"] = result.StdoutBytes,
            ["stderr_bytes"] = result.StderrBytes,
            ["stdout_dropped_bytes"] = result.StdoutDroppedBytes,
            ["stderr_dropped_bytes"] = result.StderrDroppedBytes,
            ["frames_emitted"] = result.FramesEmitted,
            ["frames_dropped"] = result.FramesDropped,
            ["working_directory"] = confineWorkspace
                ? new WorkspacePathGuard(canonicalRoot).ToRelative(result.WorkingDirectory)
                : BoundWorkingDirectory(result.WorkingDirectory),
        };
        return result.Status switch
        {
            ToolResultStatuses.Succeeded => ToolExecutionOutcome.Succeeded(
                output,
                ToolVerificationStates.NotRequired,
                "Terminal process completed."),
            ToolResultStatuses.Failed => new ToolExecutionOutcome(
                ToolResultStatuses.Failed,
                output,
                result.ExitCode is null
                    ? "process_start_failed"
                    : "process_exit_nonzero",
                "Terminal process failed.",
                ToolVerificationStates.NotRequired),
            ToolResultStatuses.TimedOut => new ToolExecutionOutcome(
                ToolResultStatuses.TimedOut,
                output,
                "process_timed_out",
                "Terminal process timed out.",
                ToolVerificationStates.NotRequired),
            ToolResultStatuses.Cancelled => new ToolExecutionOutcome(
                ToolResultStatuses.Cancelled,
                output,
                "process_cancelled",
                "Terminal process was cancelled.",
                ToolVerificationStates.NotRequired),
            _ => ToolExecutionOutcome.Failed(
                "invalid_executor_status",
                "Terminal process returned an invalid status.",
                ToolVerificationStates.NotRequired),
        };
    }

    private static string BoundWorkingDirectory(string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory))
        {
            return string.Empty;
        }

        return workingDirectory.Length > 1024 ? workingDirectory[..1024] : workingDirectory;
    }

    private static ToolExecutionOutcome Normalize(ToolBinding binding, ToolExecutionOutcome outcome)
    {
        if (!ToolResultStatuses.Known.Contains(outcome.Status))
        {
            return ToolExecutionOutcome.Failed("invalid_executor_status", "Tool returned an invalid status.");
        }

        if (!ToolVerificationStates.Known.Contains(outcome.VerificationState))
        {
            return ToolExecutionOutcome.Failed("invalid_verification_state", "Tool verification was invalid.");
        }

        if (outcome.Output.ContainsKey("status"))
        {
            return ToolExecutionOutcome.Failed("conflicting_output_status", "Tool returned a conflicting status.");
        }

        if (binding.VerificationRequired
            && outcome.Status == ToolResultStatuses.Succeeded
            && outcome.VerificationState != ToolVerificationStates.Verified)
        {
            return ToolExecutionOutcome.Failed("verification_required", "Tool success was not verified.");
        }

        if (outcome.SafeSummary.Length > 4096)
        {
            return ToolExecutionOutcome.Failed("invalid_safe_summary", "Tool summary was invalid.");
        }

        try
        {
            if (outcome.Status == ToolResultStatuses.Succeeded || outcome.Output.Count > 0)
            {
                ToolSchemaValidator.Validate(binding.Manifest.OutputSchema, outcome.Output, "invalid_tool_output");
            }
        }
        catch (ToolSchemaException error)
        {
            return ToolExecutionOutcome.Failed(error.Code, "Tool output was rejected.", VerificationFailure(binding));
        }

        var outputBytes = Encoding.UTF8.GetByteCount(outcome.Output.ToJsonString());
        if (outputBytes > binding.Manifest.MaxOutputBytes)
        {
            return ToolExecutionOutcome.Failed("output_too_large", "Tool output exceeded its limit.", VerificationFailure(binding));
        }

        return outcome;
    }
}
