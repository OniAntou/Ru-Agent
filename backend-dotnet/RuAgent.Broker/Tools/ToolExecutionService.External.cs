using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
using RuAgent.Broker.Tools.Computer;
using RuAgent.Broker.Tools.Browser;
using RuAgent.Broker.Skills;
using RuAgent.Broker.Tools.Mcp;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    /// <summary>
    /// Default page when a spool read omits limit: one head-sized window.
    /// </summary>
    private const int DefaultSpoolPageChars = 8000;

    private readonly TerminalOutputSpool _spools;

    /// <summary>
    /// Pages back full retained output of an earlier terminal call in the same
    /// session. Fail-closed value lookup: unknown, expired, or foreign-session
    /// ids are a malformed call, never an approval and never another session's
    /// text. No new capability: the session already received these bytes.
    /// Output past the in-memory window pages from the session spool file. A
    /// running background task pages live text and carries its state and exit
    /// code. <c>wait_ms</c> delays the read until the task's state or output
    /// moves, so an agent that launched background work keeps doing other work
    /// and reads once instead of polling per step.
    /// </summary>
    private async ValueTask<ToolExecutionOutcome> ExecuteTerminalReadOutputAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var spoolId = ReadOptionalString(arguments, "execution_id");
        if (string.IsNullOrWhiteSpace(spoolId)
            || spoolId.Length > TerminalLimits.MaxExecutionIdChars
            || spoolId.Contains('\0'))
        {
            throw new ArgumentException("invalid_arguments");
        }

        // Ranges are already enforced by schema validation; these are backstops.
        var offset = ReadOptionalInt(arguments, "offset") ?? 0;
        var limit = ReadOptionalInt(arguments, "limit") ?? DefaultSpoolPageChars;
        var waitMs = ReadOptionalInt(arguments, "wait_ms") ?? 0;
        if (offset < 0 || offset > TerminalLimits.MaxSpoolFileChars
            || limit < 1 || limit > TerminalLimits.MaxSpoolPageChars
            || waitMs < 0 || waitMs > TerminalLimits.TerminalReadMaxWaitMs)
        {
            throw new ArgumentException("invalid_arguments");
        }

        var waited = false;
        var changed = false;
        var waitedMs = 0;
        if (waitMs > 0 && _backgroundTasks.TryGetRevision(context.SessionId, spoolId, out var revision))
        {
            waited = true;
            var clock = Stopwatch.StartNew();
            var result = await _backgroundTasks
                .WaitForRevisionAsync(context.SessionId, spoolId, revision, waitMs, cancellationToken)
                .ConfigureAwait(false);
            waitedMs = (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds);
            changed = result?.Changed ?? false;
        }

        if (!TryReadRetainedPage(context.SessionId, spoolId, offset, limit, out var retained))
        {
            throw new ArgumentException("unknown_spool_output");
        }

        var task = retained.Task;
        var output = new JsonObject
        {
            ["spool_id"] = spoolId,
            ["offset"] = retained.Offset,
            ["limit"] = limit,
            ["total_chars"] = retained.TotalChars,
            ["page"] = retained.Text,
            ["truncated"] = retained.Offset + retained.Text.Length < retained.TotalChars,
        };
        if (retained.SpoolPath is not null)
        {
            output["spool_path"] = retained.SpoolPath;
        }

        if (retained.Truncated)
        {
            output["spool_truncated"] = true;
        }

        if (task is not null)
        {
            output["state"] = BackgroundStateWire(task.State);
            if (task.IsBackground)
            {
                output["background"] = true;
            }

            if (task.ExitCode is { } exitCode)
            {
                output["exit_code"] = exitCode;
            }
        }

        if (waited)
        {
            output["waited_ms"] = waitedMs;
            output["changed"] = changed;
        }

        return ToolExecutionOutcome.Succeeded(output, ToolVerificationStates.NotRequired, "Terminal output page.");
    }

    private static string SlicePage(string text, int offset, int limit)
    {
        var start = Math.Min(offset, text.Length);
        return text.Substring(start, Math.Min(limit, text.Length - start));
    }

    /// <summary>
    /// One retained-output page for a session-owned task or spool id: the text,
    /// the effective start, the recorded total and the task view when the
    /// registry still knows the id. A null offset selects the tail page, which
    /// is what a human reader wants; the model's read always names an offset and
    /// keeps head-based paging. Both callers share this one rule, so the console
    /// and the model never disagree about a task's text.
    /// </summary>
    private bool TryReadRetainedPage(
        string sessionId,
        string executionId,
        int? offset,
        int limit,
        out RetainedOutputPage page)
    {
        page = null!;
        BackgroundTaskSnapshot? task = null;
        if (_backgroundTasks.TryRead(sessionId, executionId, out var snapshot))
        {
            task = snapshot;
            if (snapshot.State == BackgroundTaskState.Running)
            {
                // A running task pages its live window: the spool file is still
                // being written and its tail is not yet flushed.
                page = SliceLive(task, snapshot.Text, offset, limit, snapshot.TextTruncated);
                return true;
            }
        }

        // A finished task or a plain foreground call pages from the spool when
        // it exists: the file retains more than the in-memory window.
        if (_spools.TryReadPage(sessionId, executionId, 0, 1, out var head))
        {
            var start = offset ?? Math.Max(0, head.TotalChars - limit);
            if (_spools.TryReadPage(sessionId, executionId, start, limit, out var stored))
            {
                page = new RetainedOutputPage(
                    task,
                    stored.Text,
                    Math.Clamp(start, 0, stored.TotalChars),
                    stored.TotalChars,
                    stored.FilePath,
                    head.Truncated);
                return true;
            }
        }

        if (task is null)
        {
            return false;
        }

        // The spool is gone for a task the registry still holds: the window is
        // a head, not the complete capture, and the caller must see that.
        page = SliceLive(task, task.Text, offset, limit, task.TextTruncated);
        return true;
    }

    private static RetainedOutputPage SliceLive(
        BackgroundTaskSnapshot task,
        string text,
        int? offset,
        int limit,
        bool truncated)
    {
        var start = offset ?? Math.Max(0, text.Length - limit);
        return new RetainedOutputPage(
            task,
            SlicePage(text, start, limit),
            Math.Clamp(start, 0, text.Length),
            text.Length,
            null,
            truncated);
    }

    /// <summary>Default page one typed task read returns when the caller names none.</summary>
    private const int SessionTaskPageChars = 8_000;

    /// <summary>
    /// One bounded page of a session-owned task's retained output for the typed
    /// command channel. It reuses the model's paging rule and returns already
    /// redacted text, so it grants no capability the session did not already
    /// receive.
    /// </summary>
    private JsonObject ReadSessionTaskOutput(string sessionId, SessionTaskRequest request)
    {
        var executionId = request.ReadExecutionId!;
        var limit = request.Limit ?? SessionTaskPageChars;
        if (limit < 1 || limit > TerminalLimits.MaxSpoolPageChars
            || (request.Offset is { } requestedOffset
                && (requestedOffset < 0 || requestedOffset > TerminalLimits.MaxSpoolFileChars)))
        {
            throw new ArgumentException("invalid_arguments");
        }

        if (!TryReadRetainedPage(sessionId, executionId, request.Offset, limit, out var retained))
        {
            throw new ArgumentException("unknown_spool_output");
        }

        var output = new JsonObject
        {
            ["execution_id"] = executionId,
            ["offset"] = retained.Offset,
            ["limit"] = limit,
            ["total_chars"] = retained.TotalChars,
            ["page"] = retained.Text,
            ["truncated"] = retained.Offset + retained.Text.Length < retained.TotalChars,
            ["has_before"] = retained.Offset > 0,
        };
        if (retained.SpoolPath is not null)
        {
            output["spool_path"] = retained.SpoolPath;
        }

        if (retained.Truncated)
        {
            output["spool_truncated"] = true;
        }

        if (retained.Task is { } task)
        {
            output["state"] = BackgroundStateWire(task.State);
            if (task.IsBackground)
            {
                output["background"] = true;
            }

            if (task.Command.Length > 0)
            {
                output["command"] = task.Command;
            }

            if (task.ExitCode is { } exitCode)
            {
                output["exit_code"] = exitCode;
            }
        }

        return output;
    }

    private sealed record RetainedOutputPage(
        BackgroundTaskSnapshot? Task,
        string Text,
        int Offset,
        int TotalChars,
        string? SpoolPath,
        bool Truncated);

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
    /// Session task control for the Host: the bounded task list, an optional
    /// kill, or one bounded output page. Unknown or foreign ids fail closed with
    /// the same codes as the tool legs.
    /// </summary>
    public JsonObject HandleSessionTasks(string sessionId, SessionTaskRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ReadExecutionId))
        {
            return ReadSessionTaskOutput(sessionId, request);
        }

        var killed = false;
        if (!string.IsNullOrWhiteSpace(request.KillExecutionId))
        {
            killed = _backgroundTasks.Kill(sessionId, request.KillExecutionId) switch
            {
                BackgroundKillOutcome.Killing => true,
                BackgroundKillOutcome.NotRunning => throw new ArgumentException("background_task_not_running"),
                _ => throw new ArgumentException("unknown_background_task"),
            };
        }

        var tasks = new JsonArray();
        foreach (var snapshot in _backgroundTasks.List(sessionId))
        {
            tasks.Add(new JsonObject
            {
                ["execution_id"] = snapshot.ExecutionId,
                ["state"] = BackgroundStateWire(snapshot.State),
                ["background"] = snapshot.IsBackground,
                ["exit_code"] = snapshot.ExitCode,
                ["chars"] = snapshot.Text.Length,
                ["command"] = snapshot.Command,
            });
        }

        return new JsonObject
        {
            ["tasks"] = tasks,
            ["killed"] = killed,
        };
    }

    /// <summary>
    /// Registers one terminal task and starts its pump. The pump's synchronous
    /// prefix (process create, job assign, resume) runs inline before this call
    /// returns; the rest continues detached on the registry's lifetime token,
    /// so the task outlives the tool call and dies at its lifetime cap or on
    /// mode kill. Every launch reserves a concurrent slot, since a foreground
    /// call can become a background task after its wait window expires.
    /// </summary>
    private (string ExecutionId, Task<StreamingProcessResult> Pump) StartTerminalTask(
        ToolExecutionContext context,
        int lifetimeMs,
        bool isBackground,
        string workingDirectory,
        string displayCommand,
        CancellationToken cancellationToken,
        Func<CancellationToken, Action<string>, Task<StreamingProcessResult>> run)
    {
        lock (_terminalLifetimeGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_stoppingSessions.Contains(context.SessionId))
            {
                throw new OperationCanceledException("Session terminal tasks are stopping.");
            }
            var executionId = context.RequestId;
            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new ArgumentException("invalid_arguments");
            }

            if (!_backgroundTasks.TryStart(
                    context.SessionId,
                    executionId,
                    lifetimeMs,
                    isBackground,
                    out var lifetimeToken,
                    displayCommand))
            {
                throw new ArgumentException("background_task_limit_reached");
            }

            var writer = _spools.CreateWriter(context.SessionId, executionId);
            var pump = PumpBackgroundTaskAsync(context, executionId, lifetimeToken, writer, workingDirectory, run);
            TrackTerminalPump(context.SessionId, pump);
            return (executionId, pump);
        }
    }

    private static ToolExecutionOutcome StartedBackgroundOutcome(string executionId, int lifetimeMs) =>
        ToolExecutionOutcome.Succeeded(
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

    /// <summary>
    /// Waits up to the approved window for a foreground task. A task that
    /// finishes first returns its normal receipt; one that outlives the window
    /// is moved to the background (the pump keeps running to the lifetime cap)
    /// and the call returns its execution id instead of a kill.
    /// </summary>
    private async ValueTask<ToolExecutionOutcome> AwaitTerminalTaskAsync(
        ToolExecutionContext context,
        string executionId,
        Task<StreamingProcessResult> pump,
        int waitMs,
        int lifetimeMs,
        CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(waitMs, wait.Token);
        var completed = await Task.WhenAny(pump, delay).ConfigureAwait(false);
        if (completed == pump)
        {
            wait.Cancel();
            var result = await pump.ConfigureAwait(false);
            return WithSpoolId(ProcessOutcome(result), context);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // The run was cancelled while the command was still foreground: stop
            // the task and wait for the kill to reap the tree before reporting,
            // so the receipt means the process is gone.
            _backgroundTasks.Kill(context.SessionId, executionId);
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The kill is in flight; the receipt still reports the cancellation.
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        _backgroundTasks.MarkBackground(context.SessionId, executionId);
        return ToolExecutionOutcome.Succeeded(
            new JsonObject
            {
                ["execution_id"] = executionId,
                ["moved_to_background"] = true,
                ["state"] = "running",
                ["waited_ms"] = waitMs,
                ["lifetime_ms"] = lifetimeMs,
                ["spool_id"] = executionId,
            },
            ToolVerificationStates.NotRequired,
            $"Command did not complete within its {waitMs}ms window and was moved to the background; page it with mode read_output or stop it with mode kill.");
    }

    /// <summary>
    /// A foreground task that moves to the background keeps running to the
    /// background lifetime so the move is useful, bounded by the protocol cap.
    /// </summary>
    private static int BackgroundLifetimeFor(int waitMs) =>
        Math.Clamp(
            Math.Max(waitMs, TerminalLimits.BackgroundDefaultLifetimeMs),
            1,
            TerminalLimits.BackgroundMaxLifetimeMs);

    /// <summary>
    /// Drives one terminal task to completion: streams frames into the live
    /// registry text, then records the final state and stores the spool. Runs
    /// on the registry's lifetime token, not the tool call's token, and returns
    /// the runner's result so a foreground waiter can build the normal receipt.
    /// </summary>
    private async Task<StreamingProcessResult> PumpBackgroundTaskAsync(
        ToolExecutionContext context,
        string executionId,
        CancellationToken lifetimeToken,
        SpoolFileWriter writer,
        string workingDirectory,
        Func<CancellationToken, Action<string>, Task<StreamingProcessResult>> run)
    {
        var status = ToolResultStatuses.Failed;
        int? exitCode = null;
        StreamingProcessResult? result = null;
        try
        {
            result = await run(lifetimeToken, frame =>
            {
                writer.Append(frame);
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
            writer.Complete();
            _backgroundTasks.Complete(
                context.SessionId,
                executionId,
                status,
                exitCode,
                writer.Window,
                // The registry keeps only the head; remember that output beyond it
                // existed so a read after the spool is gone still says so.
                writer.TotalChars > writer.Window.Length || writer.Truncated);
            RetainSpool(context, writer);
        }

        return result ?? new StreamingProcessResult(
            Status: status,
            ExitCode: exitCode,
            DurationMs: 0,
            StdoutHead: string.Empty,
            StdoutTail: string.Empty,
            StderrHead: string.Empty,
            StderrTail: string.Empty,
            StdoutBytes: 0,
            StderrBytes: 0,
            StdoutDroppedBytes: 0,
            StderrDroppedBytes: 0,
            FramesEmitted: 0,
            FramesDropped: 0,
            WorkingDirectory: workingDirectory);
    }

    private void RetainSpool(ToolExecutionContext context, SpoolFileWriter writer)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context.RequestId) || writer.Window.Length == 0)
            {
                return;
            }

            _spools.Store(context.SessionId, context.RequestId, writer);
        }
        finally
        {
            writer.ReleaseBudget();
        }
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteTerminalRunAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var workingDirectory = ReadOptionalString(arguments, "working_directory") ?? ".";
        var tools = ToolsFor(context);
        var grant = new PolicyGrant(
            context.SessionId,
            context.SessionScopes ?? _session.ScopesFor(context.SessionId),
            _session.ExpiresAt);
        var optionalMs = ReadOptionalInt(arguments, "timeout_ms");
        // The request carries the approved window: it is what the intent hash and
        // any one-time approval bound. The kill timer is separate.
        var requestedMs = optionalMs ?? TerminalLimits.DefaultTimeoutMs;
        var isBackground = IsTrue(arguments, "background");
        var lifetimeMs = isBackground
            ? optionalMs ?? TerminalLimits.BackgroundDefaultLifetimeMs
            : BackgroundLifetimeFor(requestedMs);
        var request = new TerminalRunRequest(
            "powershell",
            workingDirectory,
            ReadString(arguments, "executable"),
            ReadArguments(arguments["arguments"]),
            requestedMs,
            IsTrue(arguments, "network_intent") || IsTrue(arguments, "network"),
            IsTrue(arguments, "read_only"),
            ReadWriteSet(arguments),
            // Every call reaching an executor was authorized by BrokerExecutionRuntime.
            Approved: true)
        {
            AuthorizedIntent = context.AuthorizedTerminalIntent,
        };
        // Resolve before registering a task: otherwise the detached pump hides
        // actionable resolution errors behind process_start_failed, and a
        // background request can report "started" without an executable.
        if (request.AuthorizedIntent is null) _ = tools.Terminal.CanonicalizeRun(request);
        var (executionId, pump) = StartTerminalTask(
            context,
            lifetimeMs,
            isBackground,
            workingDirectory,
            tools.Terminal.DisplayCommand(TerminalRunPreview(arguments)),
            cancellationToken,
            (token, retain) => tools.Terminal.RunAsync(
                grant,
                request,
                token,
                retain,
                killTimeoutMs: lifetimeMs, output: isBackground ? null : context.TerminalOutput));
        if (isBackground)
        {
            return StartedBackgroundOutcome(executionId, lifetimeMs);
        }

        return await AwaitTerminalTaskAsync(
            context,
            executionId,
            pump,
            requestedMs,
            lifetimeMs,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteTerminalShellAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var workingDirectory = ReadOptionalString(arguments, "working_directory") ?? ".";
        var tools = ToolsFor(context);
        var optionalMs = ReadOptionalInt(arguments, "timeout_ms");
        var requestedMs = optionalMs ?? TerminalLimits.DefaultTimeoutMs;
        var isBackground = IsTrue(arguments, "background");
        var lifetimeMs = isBackground
            ? optionalMs ?? TerminalLimits.BackgroundDefaultLifetimeMs
            : BackgroundLifetimeFor(requestedMs);
        var shellRequest = new TerminalShellRequest(
            "powershell",
            workingDirectory,
            ReadString(arguments, "script"),
            requestedMs,
            IsTrue(arguments, "read_only"),
            ReadWriteSet(arguments),
            // Every call reaching an executor was authorized by BrokerExecutionRuntime.
            Approved: true,
            Network: IsTrue(arguments, "network_intent") || IsTrue(arguments, "network"))
        {
            AuthorizedIntent = context.AuthorizedTerminalIntent,
        };
        if (shellRequest.AuthorizedIntent is null) _ = tools.Terminal.CanonicalizeShell(shellRequest);
        var grant = new PolicyGrant(
            context.SessionId,
            context.SessionScopes ?? _session.ScopesFor(context.SessionId),
            _session.ExpiresAt);
        var (executionId, pump) = StartTerminalTask(
            context,
            lifetimeMs,
            isBackground,
            workingDirectory,
            tools.Terminal.DisplayCommand(shellRequest.Script),
            cancellationToken,
            (token, retain) => tools.Terminal.RunApprovedShellAsync(
                shellRequest,
                token,
                retain,
                killTimeoutMs: lifetimeMs,
                grant: grant, output: isBackground ? null : context.TerminalOutput));
        if (isBackground)
        {
            return StartedBackgroundOutcome(executionId, lifetimeMs);
        }

        return await AwaitTerminalTaskAsync(
            context,
            executionId,
            pump,
            requestedMs,
            lifetimeMs,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Names the retained output on the receipt so the model can page it back
    /// with mode read_output instead of re-running the command, and names the
    /// spill file when the output overflowed the in-memory window.
    /// </summary>
    private ToolExecutionOutcome WithSpoolId(ToolExecutionOutcome outcome, ToolExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.RequestId))
        {
            return outcome;
        }

        outcome.Output["spool_id"] = context.RequestId;
        if (_spools.TryGetSpill(context.SessionId, context.RequestId, out var filePath) && filePath is not null)
        {
            outcome.Output["spool_path"] = filePath;
        }

        return outcome;
    }

    private static string TerminalRunPreview(JsonObject arguments)
    {
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

    private ComputerUseService? _computer;

    /// <summary>Connects the Host's computer-use service; without it every <c>computer</c> call fails by name.</summary>
    public void UseComputer(ComputerUseService computer) => _computer = computer ?? throw new ArgumentNullException(nameof(computer));

    private ValueTask<ToolExecutionOutcome> ExecuteComputerAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (_computer is not { } computer)
            return ValueTask.FromResult(ToolExecutionOutcome.Failed("computer_unavailable", "Computer use is not available in this host."));
        if (string.IsNullOrEmpty(context.RunId)
            || !_session.TryGetRunLease(context.RunId, out var lease) || lease is null)
            return ValueTask.FromResult(ToolExecutionOutcome.Failed("computer_run_required", "Computer use needs an authorized run."));
        // A run that ended no longer holds the desktop, even before its idle timeout.
        if (computer.OwnerRunId is { } owner && owner != context.RunId
            && (!_session.TryGetRunLease(owner, out var ownerLease) || ownerLease is null || ownerLease.Lifetime.IsRevoked))
            computer.ReleaseRun(owner);
        // Grants and masking depend on the run's preset; a lease without one fails closed as standard.
        var fullAccess = string.Equals(lease.Snapshot.Preset, BrokerPolicyContracts.FullAccessPresetWireValue, StringComparison.Ordinal);
        return computer.ExecuteAsync(context.RunId, context.SessionId, fullAccess, context.ImageInputAccepted, arguments, cancellationToken);
    }

    private static HttpClient CreateWebHttpClient() =>
        new(WebConnectionPolicy.CreateHandler(), disposeHandler: true);

    private BrowserUseService? _browser;

    /// <summary>Connects the Host's browser service; without it every <c>browser</c> call fails by name.</summary>
    public void UseBrowser(BrowserUseService browser) => _browser = browser ?? throw new ArgumentNullException(nameof(browser));

    private async ValueTask<ToolExecutionOutcome> ExecuteBrowserAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (_browser is not { } browser)
        {
            return ToolExecutionOutcome.Failed("browser_unavailable", "Browser control is not available in this host.");
        }

        IReadOnlyList<string>? uploadPaths = null;
        if (arguments["action"] is JsonValue action
            && action.TryGetValue<string>(out var name)
            && string.Equals(name, BrowserActions.Upload, StringComparison.Ordinal))
        {
            try
            {
                uploadPaths = ResolveUploadPaths(context, arguments);
            }
            catch (ToolExecutionException error)
            {
                return ToolExecutionOutcome.Failed(error.Message, UploadFailureDetail(error.Message));
            }
        }

        var options = new BrowserCallOptions(context.ImageInputAccepted, uploadPaths, context.BrowserPersistentProfile);
        return await browser.ExecuteAsync(context.RunId, context.SessionId, arguments, cancellationToken, options).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves an approved upload's paths again at execution (ADR 0055 amendment 2026-11-25): each
    /// must still be an existing workspace file within the size bound, and a protected file is
    /// refused unless the preset opens protected paths. The policy approval bound the same files.
    /// </summary>
    private List<string> ResolveUploadPaths(ToolExecutionContext context, JsonObject arguments)
    {
        if (arguments["paths"] is not JsonArray paths || paths.Count == 0 || paths.Count > BrowserLimits.MaxUploadFiles)
        {
            throw new ToolExecutionException("browser_upload_paths_required");
        }

        var profile = ActivePolicyProfile(context);
        var root = ToolsFor(context).MutationResolver.CanonicalRoot;
        var resolver = new CanonicalPathResolver(root, allowProtectedPaths: profile is not null && AgentPolicyProfileCatalog.OpensProtectedPaths(profile));
        var resolved = new List<string>(paths.Count);
        foreach (var item in paths)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var input) || string.IsNullOrWhiteSpace(input))
            {
                throw new ToolExecutionException("browser_upload_paths_required");
            }

            ResolvedPath path;
            try
            {
                path = resolver.Resolve(input);
            }
            catch (ArgumentException)
            {
                throw new ToolExecutionException("browser_upload_path_invalid");
            }

            if (path.IsProtected) throw new ToolExecutionException("protected_path");
            if (!path.IsInsideWorkspace) throw new ToolExecutionException("browser_upload_path_invalid");
            var file = new FileInfo(path.CanonicalPath);
            if (!file.Exists) throw new ToolExecutionException("browser_upload_file_missing");
            if (file.Length > BrowserLimits.MaxUploadFileBytes) throw new ToolExecutionException("browser_upload_file_too_large");
            if (!resolved.Contains(path.CanonicalPath, StringComparer.OrdinalIgnoreCase)) resolved.Add(path.CanonicalPath);
        }

        return resolved;
    }

    private static string UploadFailureDetail(string code) => code switch
    {
        "browser_upload_paths_required" => "upload needs paths: 1 to " + BrowserLimits.MaxUploadFiles + " workspace files.",
        "browser_upload_path_invalid" => "upload takes files inside the workspace only.",
        "browser_upload_file_missing" => "An upload path is not an existing file.",
        "browser_upload_file_too_large" => "An upload file is larger than " + (BrowserLimits.MaxUploadFileBytes / (1024 * 1024)) + " MB.",
        "protected_path" => "An upload path is protected.",
        _ => "The upload paths could not be used.",
    };

    private async ValueTask<ToolExecutionOutcome> ExecuteReadAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        FileReadResult result;
        var tools = ResolveTools(context, TargetPathOf(arguments));
        try
        {
            result = await tools.Read.ExecuteAsync(arguments, context.ImageInputAccepted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileReadRefusalException error)
        {
            return ToolExecutionOutcome.Failed(error.Code, error.Hint);
        }

        // Only the plain text pager reports bytes_read/total_bytes; extracted documents and
        // images do not, so they never count as a whole-file read.
        var output = result.Output;
        var wholeFile = result.Image is null
            && TryReadInteger(output["offset"], out var offset) && offset == 0
            && output["truncated"] is JsonValue truncatedValue && truncatedValue.TryGetValue<bool>(out var truncated) && !truncated
            && TryReadInteger(output["bytes_read"], out var bytesRead)
            && TryReadInteger(output["total_bytes"], out var totalBytes)
            && bytesRead == totalBytes;
        RecordFullRead(context.SessionId, tools.MutationResolver, arguments, output, wholeFile);

        return ToolExecutionOutcome.Succeeded(
            result.Output,
            ToolVerificationStates.NotRequired,
            result.Image is null ? "Workspace file read." : "Workspace image read.") with { Image = result.Image };
    }

    private ValueTask<ToolExecutionOutcome> ExecuteListAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            ResolveTools(context, TargetPathOf(arguments)).List.Execute(arguments),
            ToolVerificationStates.NotRequired,
            "Workspace entries listed."));
    }

    private ValueTask<ToolExecutionOutcome> ExecuteHelpAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        _ = context;
        return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            _helpDocs.Execute(arguments, cancellationToken),
            ToolVerificationStates.NotRequired,
            "Bundled Ru documentation searched."));
    }

    private ValueTask<ToolExecutionOutcome> ExecuteSkillBodyAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var skillBodies = new SkillBodyStore(ToolsFor(context).MutationResolver.CanonicalRoot);
        var name = arguments["name"]?.GetValue<string>()?.Trim();
        var query = arguments["query"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(name) == string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("skill_requires_name_or_query");
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (arguments["expected_revision"] is not null || arguments["expected_body_sha256"] is not null)
                throw new ArgumentException("skill_query_does_not_accept_body_version");
            var catalog = skillBodies.Discover(query, cancellationToken: cancellationToken);
            var matching = catalog.Items;
            var invocable = matching.Where(item => item.Enabled && item.ModelInvocable).ToArray();
            var items = invocable.Take(32).Select(item => (JsonNode?)new JsonObject
            {
                ["id"] = item.Id,
                ["name"] = item.Name,
                ["description"] = item.Description,
                ["source_path"] = item.SourcePath,
                ["resource_base"] = item.ResourceBase,
                ["revision"] = item.Revision,
                ["enabled"] = item.Enabled,
                ["model_invocable"] = item.ModelInvocable,
                ["user_invocable"] = item.UserInvocable,
                ["requires"] = new JsonArray(item.Requires.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["compatibility"] = item.Compatibility,
            }).ToArray();
            var diagnostics = catalog.Diagnostics.Take(64).Select(item => (JsonNode?)new JsonObject
            {
                ["source_path"] = item.SourcePath,
                ["code"] = item.Code,
            }).ToArray();
            return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
                new JsonObject
                {
                    ["items"] = new JsonArray(items),
                    ["diagnostics"] = new JsonArray(diagnostics),
                    // Truncation means more results existed, never that a match was filtered
                    // out: `hidden` counts matching entries this caller cannot load (disabled
                    // or manual-only), so an empty page with total > 0 explains itself.
                    ["truncated"] = catalog.Truncated || catalog.Total > matching.Count || invocable.Length > items.Length,
                    ["total"] = catalog.Total,
                    ["hidden"] = matching.Count - invocable.Length,
                }, ToolVerificationStates.NotRequired, "Workspace skill catalog searched."));
        }
        var expectedRevision = arguments["expected_revision"]?.GetValue<string>();
        var expectedBodySha256 = arguments["expected_body_sha256"]?.GetValue<string>();
        if (!skillBodies.TryLoadBody(name!, out var body, out var errorCode, expectedRevision, expectedBodySha256) || body is null)
        {
            throw new UnauthorizedAccessException(errorCode ?? "skill_not_found");
        }

        var unmet = EvaluateSkillRequirements(body.Requires);
        if (unmet is not null)
        {
            return ValueTask.FromResult(unmet);
        }

        return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            new JsonObject
            {
                ["name"] = body.Name,
                ["description"] = body.Description,
                ["body"] = body.Body,
                ["body_sha256"] = body.BodySha256,
                ["id"] = body.Id,
                ["source_path"] = body.SourcePath,
                ["resource_base"] = body.ResourceBase,
                ["revision"] = body.Revision,
                ["requires"] = new JsonArray(body.Requires.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["compatibility"] = body.Compatibility,
                ["model_invocable"] = body.ModelInvocable,
                ["user_invocable"] = body.UserInvocable,
            },
            ToolVerificationStates.NotRequired,
            "Workspace skill loaded."));
    }

    private ToolExecutionOutcome? EvaluateSkillRequirements(IReadOnlyList<string> requires)
    {
        var failure = SkillRequirements.FindUnmet(requires, _skillCapabilityAvailable);
        return failure is null
            ? null
            : ToolExecutionOutcome.Failed(failure.Code, failure.Message, ToolVerificationStates.NotRequired);
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteWebSearchAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (_webSearch is null)
        {
            throw new UnauthorizedAccessException("web_disabled");
        }

        var citations = await _webSearch.SearchAsync(
            ReadString(arguments, "query"),
            ReadMaxResults(arguments),
            cancellationToken).ConfigureAwait(false);
        var output = new JsonObject
        {
            ["untrusted"] = true,
            ["citations"] = new JsonArray(citations.Select(ToJson).Cast<JsonNode?>().ToArray()),
        };
        return ToolExecutionOutcome.Succeeded(
            output,
            ToolVerificationStates.NotRequired,
            "Web search completed.");
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteWebFetchAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (_webFetch is null)
        {
            throw new UnauthorizedAccessException("web_disabled");
        }

        var rawUri = ReadString(arguments, "url");
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("web_uri_invalid");
        }

        var fetched = await _webFetch.FetchAsync(uri, cancellationToken,
            ReadOptionalInt(arguments, "offset") ?? 0,
            ReadOptionalInt(arguments, "limit") ?? WebContentSanitizer.DefaultMaxCharacters).ConfigureAwait(false);
        return ToolExecutionOutcome.Succeeded(
            new JsonObject
            {
                ["untrusted"] = true,
                ["source"] = ToJson(fetched.Source),
                ["content"] = fetched.SanitizedText,
                ["content_type"] = fetched.ContentType,
                ["offset"] = fetched.Offset,
                ["next_offset"] = fetched.NextOffset,
                ["truncated"] = fetched.NextOffset is not null,
            },
            ToolVerificationStates.NotRequired,
            "Web page fetched.");
    }

    private static JsonObject ToJson(SourceCitation citation) => new()
    {
        ["title"] = citation.Title.Length <= 256 ? citation.Title : citation.Title[..256],
        ["url"] = citation.Url.ToString(),
        ["snippet"] = citation.Snippet is null
            ? null
            : citation.Snippet.Length <= 512 ? citation.Snippet : citation.Snippet[..512],
    };

    private static int ReadMaxResults(JsonObject arguments) =>
        ReadOptionalInt(arguments, "max_results") is { } value
            ? Math.Clamp(value, 1, 10)
            : 10;

    private McpServerHost? _mcpServers;

    /// <summary>Connects the Host's MCP servers; without it every <c>mcp</c> call fails by name.</summary>
    public void UseMcpServers(McpServerHost servers) => _mcpServers = servers ?? throw new ArgumentNullException(nameof(servers));

    private async ValueTask<ToolExecutionOutcome> ExecuteMcpAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        _ = context;
        if (_mcpServers is not { } servers)
        {
            return ToolExecutionOutcome.Failed("mcp_unavailable", "MCP servers are not available in this host.");
        }

        var op = ReadOptionalString(arguments, "op");
        try
        {
            return op switch
            {
                "list_servers" => ToolExecutionOutcome.Succeeded(
                    servers.ListServers(), ToolVerificationStates.NotRequired, "MCP servers listed."),
                "list_tools" => ToolExecutionOutcome.Succeeded(
                    await servers.ListToolsAsync(Required(arguments, "server"), cancellationToken).ConfigureAwait(false),
                    ToolVerificationStates.NotRequired, "MCP tools listed."),
                "call" => ToolExecutionOutcome.Succeeded(
                    await servers.CallToolAsync(
                        Required(arguments, "server"),
                        Required(arguments, "tool"),
                        ReadOptionalString(arguments, "arguments") ?? "{}",
                        cancellationToken).ConfigureAwait(false),
                    ToolVerificationStates.NotRequired, "MCP tool completed."),
                _ => ToolExecutionOutcome.Failed("mcp_arguments_invalid", "op must be list_servers, list_tools or call."),
            };
        }
        catch (McpException error)
        {
            return ToolExecutionOutcome.Failed(error.Code, error.Detail ?? error.Code);
        }
    }

    private static string Required(JsonObject arguments, string name) =>
        ReadOptionalString(arguments, name) is { Length: > 0 } value
            ? value
            : throw new McpException("mcp_arguments_invalid", $"{name} is required.");
}
