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
    /// <summary>
    /// Default page when a spool read omits limit: one head-sized window.
    /// </summary>
    private const int DefaultSpoolPageChars = 8000;

    private readonly TerminalOutputSpool _spools = new();
    private readonly BackgroundTaskRegistry _backgroundTasks = new();

    private async ValueTask<ToolExecutionOutcome> ExecuteBashAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var mode = ReadOptionalString(arguments, "mode") ?? "run";
        if (string.Equals(mode, "read_output", StringComparison.Ordinal))
        {
            return ExecuteTerminalReadOutput(context, arguments);
        }

        if (string.Equals(mode, "kill", StringComparison.Ordinal))
        {
            return ExecuteTerminalKill(context, arguments);
        }

        if (string.Equals(mode, "shell", StringComparison.Ordinal))
        {
            return await ExecuteTerminalShellAsync(context, arguments, cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteTerminalRunAsync(context, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pages back full retained output of an earlier terminal call in the same
    /// session. Fail-closed value lookup: unknown, expired, or foreign-session
    /// ids are a malformed call, never an approval and never another session's
    /// text. No new capability: the session already received these bytes. A
    /// background task pages live text and carries its state and exit code.
    /// </summary>
    private ToolExecutionOutcome ExecuteTerminalReadOutput(ToolExecutionContext context, JsonObject arguments)
    {
        var spoolId = ReadOptionalString(arguments, "execution_id");
        if (string.IsNullOrWhiteSpace(spoolId)
            || spoolId.Length > TerminalLimits.MaxExecutionIdChars
            || spoolId.Contains('\0'))
        {
            throw new ArgumentException("invalid_arguments");
        }

        BackgroundTaskSnapshot? task = null;
        string text;
        if (_backgroundTasks.TryRead(context.SessionId, spoolId, out var snapshot))
        {
            task = snapshot;
            text = snapshot.Text;
            if (snapshot.State != BackgroundTaskState.Running
                && text.Length == 0
                && _spools.TryRead(context.SessionId, spoolId, out var completedText))
            {
                text = completedText;
            }
        }
        else if (!_spools.TryRead(context.SessionId, spoolId, out text))
        {
            throw new ArgumentException("unknown_spool_output");
        }

        // Ranges are already enforced by schema validation; these are backstops.
        var offset = ReadOptionalInt(arguments, "offset") ?? 0;
        var limit = ReadOptionalInt(arguments, "limit") ?? DefaultSpoolPageChars;
        if (offset < 0 || offset > TerminalLimits.MaxSpoolChars
            || limit < 1 || limit > TerminalLimits.MaxSpoolPageChars)
        {
            throw new ArgumentException("invalid_arguments");
        }

        var start = Math.Min(offset, text.Length);
        var page = text.Substring(start, Math.Min(limit, text.Length - start));
        var output = new JsonObject
        {
            ["spool_id"] = spoolId,
            ["offset"] = offset,
            ["limit"] = limit,
            ["total_chars"] = text.Length,
            ["page"] = page,
            ["truncated"] = start + page.Length < text.Length,
        };
        if (task is not null)
        {
            output["background"] = true;
            output["state"] = BackgroundStateWire(task.State);
            if (task.ExitCode is { } exitCode)
            {
                output["exit_code"] = exitCode;
            }
        }

        return ToolExecutionOutcome.Succeeded(output, ToolVerificationStates.NotRequired, "Terminal output page.");
    }

    /// <summary>
    /// Stops one background task the same session started. No new capability:
    /// the task was already authorized at start, so the kill classifies as a
    /// read and only reaches session-owned task ids.
    /// </summary>
    private ToolExecutionOutcome ExecuteTerminalKill(ToolExecutionContext context, JsonObject arguments)
    {
        var executionId = ReadOptionalString(arguments, "execution_id");
        if (string.IsNullOrWhiteSpace(executionId)
            || executionId.Length > TerminalLimits.MaxExecutionIdChars
            || executionId.Contains('\0'))
        {
            throw new ArgumentException("invalid_arguments");
        }

        return _backgroundTasks.Kill(context.SessionId, executionId) switch
        {
            BackgroundKillOutcome.Killing => ToolExecutionOutcome.Succeeded(
                new JsonObject { ["execution_id"] = executionId, ["state"] = "killing" },
                ToolVerificationStates.NotRequired,
                "Background terminal task stop requested."),
            BackgroundKillOutcome.NotRunning => throw new ArgumentException("background_task_not_running"),
            _ => throw new ArgumentException("unknown_background_task"),
        };
    }

    private static string BackgroundStateWire(BackgroundTaskState state) => state switch
    {
        BackgroundTaskState.Running => "running",
        BackgroundTaskState.Exited => "exited",
        BackgroundTaskState.Killed => "killed",
        BackgroundTaskState.Expired => "expired",
        _ => "failed",
    };

    /// <summary>
    /// Starts one background task and returns its id immediately; the pump runs
    /// on the registry's lifetime token, not the tool call's token, so the task
    /// outlives the call and dies at its lifetime cap or on mode kill.
    /// </summary>
    private ToolExecutionOutcome StartBackgroundTask(
        ToolExecutionContext context,
        JsonObject arguments,
        Func<CancellationToken, Action<string>, Task<StreamingProcessResult>> run)
    {
        var lifetimeMs = ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.BackgroundDefaultLifetimeMs;
        var executionId = context.RequestId;
        if (string.IsNullOrWhiteSpace(executionId))
        {
            throw new ArgumentException("invalid_arguments");
        }

        if (!_backgroundTasks.TryStart(context.SessionId, executionId, lifetimeMs, out var lifetimeToken))
        {
            throw new ArgumentException("background_task_limit_reached");
        }

        var retained = new StringBuilder();
        // The pump's synchronous prefix (process create, job assign, resume)
        // runs inline before this call returns; the rest continues detached.
        _ = PumpBackgroundTaskAsync(context, executionId, lifetimeToken, retained, run);

        return ToolExecutionOutcome.Succeeded(
            new JsonObject
            {
                ["execution_id"] = executionId,
                ["background"] = true,
                ["state"] = "running",
                ["lifetime_ms"] = lifetimeMs,
                ["spool_id"] = executionId,
            },
            ToolVerificationStates.NotRequired,
            "Background terminal task started.");
    }

    /// <summary>
    /// Drives one background task to completion: streams frames into the live
    /// registry text, then records the final state and stores the spool. Runs
    /// on the registry's lifetime token, not the tool call's token.
    /// </summary>
    private async Task PumpBackgroundTaskAsync(
        ToolExecutionContext context,
        string executionId,
        CancellationToken lifetimeToken,
        StringBuilder retained,
        Func<CancellationToken, Action<string>, Task<StreamingProcessResult>> run)
    {
        var status = ToolResultStatuses.Failed;
        int? exitCode = null;
        try
        {
            var result = await run(lifetimeToken, frame =>
            {
                AppendSpoolCapped(retained, frame);
                _backgroundTasks.Append(context.SessionId, executionId, frame);
            }).ConfigureAwait(false);
            status = result.Status;
            exitCode = result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            status = ToolResultStatuses.Cancelled;
        }
        catch (Exception)
        {
            status = ToolResultStatuses.Failed;
        }
        finally
        {
            _backgroundTasks.Complete(context.SessionId, executionId, status, exitCode, retained.ToString());
            RetainSpool(context, retained);
        }
    }

    private static void AppendSpoolCapped(StringBuilder retained, string frame)
    {
        if (retained.Length >= TerminalLimits.MaxSpoolChars || string.IsNullOrEmpty(frame))
        {
            return;
        }

        retained.Append(frame, 0, Math.Min(frame.Length, TerminalLimits.MaxSpoolChars - retained.Length));
    }

    private void RetainSpool(ToolExecutionContext context, StringBuilder retained)
    {
        if (!string.IsNullOrWhiteSpace(context.RequestId) && retained.Length > 0)
        {
            _spools.Store(context.SessionId, context.RequestId, retained.ToString());
        }
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteTerminalRunAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var workingDirectory = ReadOptionalString(arguments, "working_directory") ?? ".";
        var tools = ResolveRootToolSet(context, workingDirectory);
        var grant = new PolicyGrant(
            context.SessionId,
            context.SessionScopes ?? _session.ScopesFor(context.SessionId),
            _session.Descriptor.ExpiresAt);
        var retained = new StringBuilder();
        if (IsTrue(arguments, "background"))
        {
            var backgroundRequest = new TerminalRunRequest(
                ReadOptionalString(arguments, "shell") ?? "powershell",
                workingDirectory,
                ReadString(arguments, "executable"),
                ReadArguments(arguments["arguments"]),
                ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.BackgroundDefaultLifetimeMs,
                IsTrue(arguments, "network_intent") || IsTrue(arguments, "network"),
                context.ApprovalToken ?? string.Empty,
                context.RequestId,
                context.SessionId,
                IsTrue(arguments, "read_only"),
                ReadWriteSet(arguments),
                Approved: context.Approved);
            return StartBackgroundTask(
                context,
                arguments,
                (token, retain) => tools.Terminal.RunAsync(
                    grant,
                    backgroundRequest,
                    context.ConfinementRequired,
                    token,
                    retain));
        }

        var result = await tools.Terminal.RunAsync(
            grant,
            new TerminalRunRequest(
                ReadOptionalString(arguments, "shell") ?? "powershell",
                workingDirectory,
                ReadString(arguments, "executable"),
                ReadArguments(arguments["arguments"]),
                ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.DefaultTimeoutMs,
                IsTrue(arguments, "network_intent") || IsTrue(arguments, "network"),
                context.ApprovalToken ?? string.Empty,
                context.RequestId,
                context.SessionId,
                IsTrue(arguments, "read_only"),
                ReadWriteSet(arguments),
                Approved: context.Approved),
            context.ConfinementRequired,
            cancellationToken,
            retainOutput: frame => AppendSpoolCapped(retained, frame)).ConfigureAwait(false);
        RetainSpool(context, retained);
        return WithSpoolId(ProcessOutcome(result, context.ConfinementRequired, tools.CanonicalRoot), context);
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteTerminalShellAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        // Shell scripts always require explicit approval: the evaluator asks with
        // terminal_unknown_executable and the execution leg consumes the same
        // one-time token. Confinement is defense in depth, not the gate.
        if (!context.Approved && context.ApprovalToken is null)
        {
            throw new UnauthorizedAccessException("terminal_shell_approval_invalid");
        }

        var workingDirectory = ReadOptionalString(arguments, "working_directory") ?? ".";
        var tools = ResolveRootToolSet(context, workingDirectory);
        var retained = new StringBuilder();
        if (IsTrue(arguments, "background"))
        {
            var backgroundShellRequest = new TerminalShellRequest(
                ReadOptionalString(arguments, "shell") ?? "powershell",
                workingDirectory,
                ReadString(arguments, "script"),
                ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.BackgroundDefaultLifetimeMs,
                context.ApprovalToken ?? string.Empty,
                context.RequestId,
                context.SessionId,
                IsTrue(arguments, "read_only"),
                ReadWriteSet(arguments),
                Approved: context.Approved);
            return StartBackgroundTask(
                context,
                arguments,
                (token, retain) => tools.Terminal.RunApprovedShellAsync(
                    backgroundShellRequest,
                    context.ConfinementRequired,
                    token,
                    retain));
        }

        var shellRequest = new TerminalShellRequest(
            ReadOptionalString(arguments, "shell") ?? "powershell",
            workingDirectory,
            ReadString(arguments, "script"),
            ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.DefaultTimeoutMs,
            context.ApprovalToken ?? string.Empty,
            context.RequestId,
            context.SessionId,
            IsTrue(arguments, "read_only"),
            ReadWriteSet(arguments),
            Approved: context.Approved);
        var result = await tools.Terminal.RunApprovedShellAsync(
            shellRequest,
            context.ConfinementRequired,
            cancellationToken,
            retainOutput: frame => AppendSpoolCapped(retained, frame)).ConfigureAwait(false);
        RetainSpool(context, retained);
        return WithSpoolId(ProcessOutcome(result, context.ConfinementRequired, tools.CanonicalRoot), context);
    }

    /// <summary>
    /// Names the retained output on the receipt so the model can page it back
    /// with mode read_output instead of re-running the command.
    /// </summary>
    private static ToolExecutionOutcome WithSpoolId(ToolExecutionOutcome outcome, ToolExecutionContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.RequestId))
        {
            outcome.Output["spool_id"] = context.RequestId;
        }

        return outcome;
    }

    /// <summary>
    /// Resolves the canonical intent hash for a terminal call without executing it.
    /// Approval tokens bind to this hash so any change to the resolved executable,
    /// argv, working directory, operation class, network intent, or timeout between
    /// approval and execution invalidates the authorization.
    /// </summary>
    private string? TryComputeTerminalIntentHash(string toolId, JsonObject arguments) =>
        TryComputeTerminalIntentHash(_defaultTools, toolId, arguments);

    private string? TryComputeTerminalIntentHash(RootToolSet tools, string toolId, JsonObject arguments)
    {
        try
        {
            var mode = ReadOptionalString(arguments, "mode")
                ?? (toolId == "bash" ? "run" : null);
            if (string.Equals(mode, "shell", StringComparison.Ordinal) || toolId == "terminal.shell")
            {
                return tools.Terminal.CanonicalizeShell(new TerminalShellRequest(
                    ReadOptionalString(arguments, "shell") ?? "powershell",
                    ReadOptionalString(arguments, "working_directory") ?? ".",
                    ReadString(arguments, "script"),
                    ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.DefaultTimeoutMs,
                    string.Empty)).IntentHash;
            }

            return toolId is "terminal.run" or "bash"
                ? tools.Terminal.CanonicalizeRun(new TerminalRunRequest(
                    "powershell",
                    ReadOptionalString(arguments, "working_directory") ?? ".",
                    ReadString(arguments, "executable"),
                    ReadArguments(arguments["arguments"]),
                    ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.DefaultTimeoutMs,
                    IsTrue(arguments, "network_intent") || IsTrue(arguments, "network"))).IntentHash
                : null;
        }
        catch (Exception error) when (
            error is ArgumentException or UnauthorizedAccessException or KeyNotFoundException
                or ToolExecutionException)
        {
            return null;
        }
    }

    private static string TerminalRunPreview(JsonObject arguments)
    {
        // A spool read names retained output, not a command: the preview says
        // so instead of rendering an empty command line.
        if (string.Equals(ReadOptionalString(arguments, "mode"), "read_output", StringComparison.Ordinal))
        {
            return "Read retained terminal output.";
        }

        var executable = ReadOptionalString(arguments, "executable");
        if (string.IsNullOrWhiteSpace(executable))
        {
            return string.Empty;
        }

        var parts = new List<string> { executable };
        if (arguments["arguments"] is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is { } node && node.GetValueKind() == System.Text.Json.JsonValueKind.String)
                {
                    parts.Add(node.GetValue<string>());
                }
            }
        }

        return string.Join(' ', parts);
    }
}
