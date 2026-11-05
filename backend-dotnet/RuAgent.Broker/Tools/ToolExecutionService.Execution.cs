using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Diagnostics;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
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
    /// <summary>
    /// The only execution entry point. <see cref="BrokerExecutionRuntime"/> calls it
    /// after it has evaluated the run's policy snapshot and consumed the single-use
    /// authorization token, so every call here is already authorized.
    /// </summary>
    internal Task<ToolResult> ExecuteAuthenticatedV2Async(
        AgentEnvelope request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(request, cancellationToken);

    internal Task<ToolResult> ExecuteAuthenticatedV2Async(
        AgentEnvelope request, CanonicalCommandIntent intent, CancellationToken cancellationToken, TerminalOutputDeltaHandler? output = null) =>
        ExecuteAsync(request, cancellationToken, intent, output);

    private async Task<ToolResult> ExecuteAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken,
        CanonicalCommandIntent? authorizedTerminalIntent = null,
        TerminalOutputDeltaHandler? output = null)
    {
        // Two counters, one funnel: every tool entry point ends here, so a call is
        // counted once whatever route reached it. The read counter answers the
        // question a dedup would later solve — how often a session re-reads a path.
        var toolId = ReadString(request.Payload, "tool_id");
        if (toolId is "read" or "read_file"
            && request.Payload["arguments"] is JsonObject arguments
            && arguments["path"] is JsonValue pathValue
            && pathValue.TryGetValue<string>(out var path)
            && !string.IsNullOrWhiteSpace(path))
        {
            BrokerTelemetry.RecordReadPage(toolId, _readRepeats.Observe(request.SessionId, path.Trim()));
        }

        var result = await ExecuteCoreAsync(
            request, cancellationToken, authorizedTerminalIntent, output).ConfigureAwait(false);
        BrokerTelemetry.RecordToolCall(toolId, result);
        return result;
    }

    private async Task<ToolResult> ExecuteCoreAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken,
        CanonicalCommandIntent? authorizedTerminalIntent = null,
        TerminalOutputDeltaHandler? output = null)
    {
        var started = Stopwatch.StartNew();
        // Fail closed: authorization is bound to an authenticated v2 tool.execute
        // envelope. Any other envelope shape never reaches an adapter.
        if (request.ProtocolVersion != AgentEnvelopeCodec.ProtocolVersionV2
            || request.MessageType != AgentMessageTypes.ToolExecute)
        {
            return Result(
                request,
                ToolResultStatuses.Denied,
                new JsonObject(),
                "tool_execute_v2_required",
                "Tool execution requires an authorized v2 tool.execute request.",
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }

        try
        {
            var toolId = ReadString(request.Payload, "tool_id");
            var toolVersion = ReadString(request.Payload, "tool_version");
            var arguments = request.Payload["arguments"] is JsonObject objectNode
                ? objectNode
                : throw new ToolExecutionException("invalid_arguments");
            var binding = _catalog.Registry.Get(toolId, toolVersion);
            var decision = EvaluateSessionGrant(request.SessionId, binding.Manifest);
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
                ValidateTerminalLaunchArguments(toolId, arguments);
            }
            catch (ToolSchemaException error)
            {
                return Result(
                    request,
                    ToolResultStatuses.Failed,
                    new JsonObject(),
                    error.Code,
                    ArgumentRepairHint(binding, arguments),
                    started,
                    ToolVerificationStates.NotRequired,
                    adapterInvoked: false);
            }

            // An approval-requiring grant is not re-asked here: the runtime already
            // resolved ask/allow against the run's policy snapshot before dispatch.
            return await ExecuteBindingAsync(
                request,
                binding,
                arguments,
                ReadOptionalString(request.Payload, "run_id") ?? request.SessionId,
                cancellationToken, authorizedTerminalIntent, output).ConfigureAwait(false);
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

    private ToolPolicyDecision EvaluateSessionGrant(string sessionId, ToolManifest manifest)
    {
        var grant = new PolicyGrant(sessionId, _session.ScopesFor(sessionId), _session.ExpiresAt);
        return _policy.Evaluate(grant, manifest);
    }

    /// <summary>
    /// The schema accepts the background lifetime ceiling so one schema serves
    /// both modes; the foreground hard maximum stays enforced here, before any
    /// adapter work, for callers that reach execution without policy
    /// evaluation.
    /// </summary>
    private static void ValidateTerminalLaunchArguments(string toolId, JsonObject arguments)
    {
        if (!IsTerminalLaunch(toolId))
        {
            return;
        }
        if (!TerminalWorkflowIntent.HasValidExpectedExitCode(arguments))
            throw new ToolSchemaException("invalid_arguments");

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

    // Launch lifetime is owned by the terminal runner, independently of the
    // envelope's control-RPC deadline. Output reads and stops keep that deadline.
    private static bool IsTerminalLaunch(string toolId) => toolId is "powershell" or "run_process";

    private async Task<ToolResult> ExecuteBindingAsync(
        AgentEnvelope request,
        ToolBinding binding,
        JsonObject arguments,
        string runId,
        CancellationToken cancellationToken,
        CanonicalCommandIntent? authorizedTerminalIntent = null,
        TerminalOutputDeltaHandler? output = null)
    {
        var started = Stopwatch.StartNew();
        try
        {
            ToolSchemaValidator.Validate(binding.Manifest.InputSchema, arguments);
            ValidateTerminalLaunchArguments(ReadString(request.Payload, "tool_id"), arguments);
        }
        catch (ToolSchemaException error)
        {
            return Result(
                request,
                ToolResultStatuses.Failed,
                new JsonObject(),
                error.Code,
                ArgumentRepairHint(binding, arguments),
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: false);
        }

        if (cancellationToken.IsCancellationRequested)
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

        var sessionScopes = _session.ScopesFor(request.SessionId);
        var context = new ToolExecutionContext(
            request.RequestId,
            request.SessionId,
            request.CorrelationId,
            ReadCapabilityClass(request.Payload),
            request.DeadlineMs,
            sessionScopes,
            runId,
            ImageInputAccepted: ReadImageInputAccepted(request.Payload))
        {
            AuthorizedTerminalIntent = authorizedTerminalIntent,
            TerminalOutput = output,
        };
        ToolExecutionOutcome outcome;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!IsTerminalLaunch(ReadString(request.Payload, "tool_id")))
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
        catch (TimeoutException error) when (error.Message == "process_timed_out")
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
            outcome = ToolExecutionOutcome.Failed(error.Code, "Tool output was rejected.", ToolVerificationStates.NotRequired);
        }
        catch (ToolExecutionException error)
        {
            outcome = ToolExecutionOutcome.Failed(error.Code, "Tool arguments were rejected.", ToolVerificationStates.NotRequired);
        }
        catch (UnauthorizedAccessException error)
        {
            outcome = ToolExecutionOutcome.Failed(
                StableCode(error.Message, "access_denied"),
                "Tool access was denied.",
                ToolVerificationStates.NotRequired);
        }
        catch (ArgumentException error)
        {
            outcome = ToolExecutionOutcome.Failed(
                StableCode(error.Message, "invalid_arguments"),
                error.Message is "read_line_too_long" or "read_line_scan_limit" ? IoRepairHint(error.Message)
                    : error.Message == "edit_overlap" ? "Edits overlap in the original file. Combine them into one exact replacement or use disjoint spans."
                    : error.Message == "invalid_path" ? "Send a plain workspace-relative path string. Do not include XML wrappers such as </rfl:parameter> in path or pattern. Correct the argument; no path was rewritten or guessed."
                    : "Tool arguments were rejected.",
                ToolVerificationStates.NotRequired);
        }
        catch (IOException error)
        {
            outcome = ToolExecutionOutcome.Failed(
                StableCode(error.Message, "io_error"),
                IoRepairHint(error.Message),
                ToolVerificationStates.NotRequired);
        }
        catch (InvalidDataException error) when (binding.Manifest.Id is "web_search" or "web_fetch")
        {
            var code = StableCode(error.Message, "invalid_data");
            outcome = ToolExecutionOutcome.Failed(code, WebFailureHints.For(code), ToolVerificationStates.NotRequired);
        }
        catch (InvalidDataException error)
        {
            outcome = ToolExecutionOutcome.Failed(
                StableCode(error.Message, "invalid_data"),
                "Tool response data was rejected.",
                ToolVerificationStates.NotRequired);
        }
        catch (EditMatchException error)
        {
            // An edit that matched zero or an unexpected number of spans keeps
            // its stable code but tells the model how to repair the call. Only
            // the match count travels; no file content leaks into the receipt.
            outcome = ToolExecutionOutcome.Failed(
                error.Code,
                error.RepairHint ?? (error.MatchCount > 0
                    ? $"old_string matched {error.MatchCount} times. Set expected_occurrences to {error.MatchCount} to replace all, or narrow old_string to a unique span."
                    : "old_string was not found in the file. Re-read the file and copy the exact text including whitespace."),
                ToolVerificationStates.NotRequired);
        }
        catch (HttpRequestException error) when (binding.Manifest.Id is "web_search" or "web_fetch")
        {
            var code = error.StatusCode is { } status ? $"web_http_{(int)status}" : "web_network_error";
            outcome = ToolExecutionOutcome.Failed(code,
                WebFailureHints.ForHttpStatus(error.StatusCode is { } known ? (int)known : null),
                ToolVerificationStates.NotRequired);
        }
        catch (Exception error)
        {
            LogAdapterFailure(request, error);
            outcome = ToolExecutionOutcome.Failed(
            "adapter_error",
            "Tool adapter failed. The broker diagnostic log carries the adapter error.",
            ToolVerificationStates.NotRequired);
        }

        if (binding.Manifest.Id is "powershell" or "run_process" && context.AuthorizedTerminalIntent is { } terminalIntent)
            AddVerificationIdentity(outcome.Output, terminalIntent);
        outcome = outcome with
        {
            DurationMs = checked((int)Math.Min(int.MaxValue, started.ElapsedMilliseconds)),
        };
        return Receipt(request, outcome, adapterInvoked: true);
    }

    private static ToolExecutionOutcome ProcessOutcome(StreamingProcessResult result)
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
            ["working_directory"] = BoundWorkingDirectory(result.WorkingDirectory),
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
            return ToolExecutionOutcome.Failed(error.Code, "Tool output was rejected.", ToolVerificationStates.NotRequired);
        }

        var outputBytes = Encoding.UTF8.GetByteCount(outcome.Output.ToJsonString());
        if (outputBytes > binding.Manifest.MaxOutputBytes)
        {
            return ToolExecutionOutcome.Failed("output_too_large", "Tool output exceeded its limit.", ToolVerificationStates.NotRequired);
        }

        return outcome;
    }
}
