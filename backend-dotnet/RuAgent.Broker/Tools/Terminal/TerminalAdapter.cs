using RuAgent.Broker.Policy;
using RuAgent.Broker.Policy.Commands;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// Legacy adapter callers have no run-bound v2 policy token. These recognized
/// classes retain their compatibility behavior; production v2 authorization is
/// decided by PolicySnapshotEvaluator and consumed by BrokerExecutionRuntime.
/// </summary>
public static class TerminalAutoPolicy
{
    public static bool IsAutoExecutable(TerminalOperationClass operationClass) =>
        operationClass is TerminalOperationClass.ReadOnly
            or TerminalOperationClass.Test
            or TerminalOperationClass.Build
            or TerminalOperationClass.Lint
            or TerminalOperationClass.GitMutation;
}

/// <summary>
/// The v2 terminal executor. Every call is canonicalized into a <see cref="CanonicalCommandIntent"/>
/// whose hash binds the resolved executable, argv, canonical working directory, operation class,
/// network intent, and timeout. Production v2 calls carry Broker-validated authorization;
/// legacy callers use the recognized classes or an exact single-use approval token.
/// Execution streams through
/// the <see cref="StreamingProcessRunner"/> which owns one Job Object per call.
/// </summary>
public sealed class TerminalAdapter
{
    private readonly WorkspacePathGuard _workspace;
    private readonly ApprovalTokenService? _approvalTokens;
    private readonly AgentCommandEnvironment _environment;
    private readonly StreamingProcessRunner _runner;
    private readonly CommandIntentResolver _resolver;
    private readonly CommandAnalyzerRegistry _analyzers;

    public TerminalAdapter(
        WorkspacePathGuard workspace,
        ApprovalTokenService? approvalTokens = null,
        AgentCommandEnvironment? commandEnvironment = null,
        StreamingProcessRunner? runner = null,
        CommandIntentResolver? intentResolver = null,
        CommandAnalyzerRegistry? analyzers = null)
    {
        _workspace = workspace;
        _approvalTokens = approvalTokens;
        _environment = commandEnvironment ?? new AgentCommandEnvironment();
        _runner = runner ?? new StreamingProcessRunner(
            _environment.GetRedactionSeeds(),
            TerminalLimits.MaxSpoolFileBytes);
        _resolver = intentResolver ?? new CommandIntentResolver(_workspace.Root);
        _analyzers = analyzers ?? new CommandAnalyzerRegistry();
    }

    /// <summary>Same credential redaction as terminal output, before retaining a bounded UI label.</summary>
    internal string DisplayCommand(string command)
    {
        var redactor = new ChunkRedactor(_environment.GetRedactionSeeds());
        string safe = redactor.Push(command) + redactor.Flush();
        safe = safe.Replace('\r', ' ').Replace('\n', ' ');
        return safe.Length <= 512 ? safe : safe[..511] + "…";
    }

    /// <summary>
    /// Resolves and classifies a bounded run request without executing anything.
    /// Structural violations throw <see cref="UnauthorizedAccessException"/> with a stable code.
    /// </summary>
    public CanonicalCommandIntent CanonicalizeRun(TerminalRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var intent = _resolver.ResolveRun(new CanonicalCommandRequest(
                request.Executable,
                request.Arguments,
                request.RelativeWorkingDirectory,
                request.TimeoutMs,
                request.Network));
            var analysis = _analyzers.Analyze(intent.ResolvedExecutable, intent.Arguments);
            return intent.Classified(analysis.OperationClass);
        }
        catch (CommandIntentException error)
        {
            throw new UnauthorizedAccessException(error.Code);
        }
    }

    /// <summary>
    /// Resolves and classifies a bounded shell request without executing anything.
    /// </summary>
    public CanonicalCommandIntent CanonicalizeShell(TerminalShellRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return _resolver.ResolveShell(new CanonicalShellRequest(
                request.Script,
                request.RelativeWorkingDirectory,
                request.TimeoutMs,
                ShellRuntime: request.Shell));
        }
        catch (CommandIntentException error)
        {
            throw new UnauthorizedAccessException(error.Code);
        }
    }

    public Task<StreamingProcessResult> RunAsync(
        PolicyGrant grant,
        TerminalRunRequest request,
        bool confineWorkspace,
        CancellationToken cancellationToken,
        Action<string>? retainOutput = null,
        int? killTimeoutMs = null,
        TerminalOutputDeltaHandler? output = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(request);
        ValidateWriteSet(request.ReadOnly, request.WriteSet);
        var intent = request.AuthorizedIntent ?? CanonicalizeRun(request);
        EnforceWorkingDirectoryPolicy(intent, confineWorkspace);

        var networkAuthorized = IsNetworkAuthorized(
            grant, request.Network, request.Approved, request.ApprovalToken);
        if (request.Network && !networkAuthorized)
        {
            throw new UnauthorizedAccessException("terminal_network_requires_approval");
        }

        if (!TerminalAutoPolicy.IsAutoExecutable(intent.OperationClass))
        {
            if (request.Approved)
            {
                // V2 authorization was already validated and consumed by the Broker connection.
            }
            else
            {
                // Non-auto forms execute only with a one-time token bound to this exact intent.
                // A missing token keeps the legacy unregistered-command rejection; a present-but-
                // invalid token means the authorization was stale, replayed, or bound to a
                // different canonical intent.
                if (_approvalTokens is null || string.IsNullOrWhiteSpace(request.ApprovalToken))
                {
                    throw new UnauthorizedAccessException("terminal_command_not_allowed");
                }

                if (string.IsNullOrWhiteSpace(request.RequestId)
                    || string.IsNullOrWhiteSpace(request.SessionId)
                    || !_approvalTokens.TryConsume(
                        request.ApprovalToken,
                        new ApprovalBinding(
                            request.RequestId,
                            request.SessionId,
                            "run_process",
                            "1.0.0",
                            intent.IntentHash,
                            DateTimeOffset.UtcNow),
                        out _))
                {
                    throw new UnauthorizedAccessException("terminal_approval_invalid");
                }
            }
        }

        return ExecuteIntentAsync(
            intent,
            cancellationToken,
            retainOutput,
            killTimeoutMs, output);
    }

    /// <summary>
    /// The single authorized-network condition: a declared network intent is
    /// authorized by the terminal-network scope, an approved execution, or a
    /// one-time approval token. This is request policy, not OS network isolation.
    /// </summary>
    internal static bool IsNetworkAuthorized(
        PolicyGrant? grant,
        bool networkIntent,
        bool approved,
        string? approvalToken) =>
        networkIntent
        && ((grant?.Scopes.Contains(ToolScope.TerminalNetwork) ?? false)
            || approved
            || !string.IsNullOrWhiteSpace(approvalToken));

    public async Task<StreamingProcessResult> RunApprovedShellAsync(
        TerminalShellRequest request,
        bool confineWorkspace,
        CancellationToken cancellationToken,
        Action<string>? retainOutput = null,
        int? killTimeoutMs = null,
        PolicyGrant? grant = null,
        TerminalOutputDeltaHandler? output = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateWriteSet(request.ReadOnly, request.WriteSet);
        if (string.IsNullOrWhiteSpace(request.Script) || request.Script.Length > TerminalLimits.MaxScriptChars)
        {
            throw new ArgumentException("terminal_script_invalid", nameof(request));
        }

        var intent = request.AuthorizedIntent ?? CanonicalizeShell(request);
        EnforceWorkingDirectoryPolicy(intent, confineWorkspace);

        // V2 authorization (automatic or approved) has already been consumed by
        // the Broker. Legacy callers require an exact, single-use shell token.
        EnsureShellApprovalTokenValid(request);

        return await _runner.RunAsync(
            new ProcessRunSpec(
                intent.ResolvedExecutable,
                intent.Arguments,
                intent.CanonicalWorkingDirectory,
                _environment.ChildEnvironment,
                killTimeoutMs ?? intent.TimeoutMs,
                TerminalLimits.MaxSpoolFileBytes),
            WrapRetain(retainOutput, output),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StreamingProcessResult> ExecuteIntentAsync(
        CanonicalCommandIntent intent,
        CancellationToken cancellationToken,
        Action<string>? retainOutput = null,
        int? killTimeoutMs = null,
        TerminalOutputDeltaHandler? output = null)
    {
        var arguments = IsAutomaticGitRead(intent)
            ? BuildSafeGitReadArguments(intent.Arguments)
            : intent.Arguments;

        return await _runner.RunAsync(
            new ProcessRunSpec(
                intent.ResolvedExecutable,
                arguments,
                intent.CanonicalWorkingDirectory,
                _environment.ChildEnvironment,
                killTimeoutMs ?? intent.TimeoutMs,
                TerminalLimits.MaxSpoolFileBytes),
            WrapRetain(retainOutput, output),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAutomaticGitRead(CanonicalCommandIntent intent) =>
        intent.OperationClass == TerminalOperationClass.ReadOnly
        && string.Equals(
            GitCommandAnalyzer.BaseName(intent.ResolvedExecutable),
            "git",
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> BuildSafeGitReadArguments(IReadOnlyList<string> arguments)
    {
        var verbIndex = 0;
        while (verbIndex < arguments.Count)
        {
            if (arguments[verbIndex] == "--no-optional-locks")
            {
                verbIndex++;
                continue;
            }

            if (arguments[verbIndex] == "-c" && verbIndex + 1 < arguments.Count)
            {
                verbIndex += 2;
                continue;
            }

            break;
        }

        var safe = new List<string>(arguments.Count + 8);
        safe.AddRange(arguments.Take(verbIndex));
        safe.AddRange(["-c", "core.fsmonitor=false", "-c", "diff.external=", "--no-pager"]);
        if (verbIndex < arguments.Count)
        {
            var verb = arguments[verbIndex];
            safe.Add(verb);
            if (verb is "diff" or "log" or "show")
            {
                safe.AddRange(["--no-ext-diff", "--no-textconv"]);
            }

            safe.AddRange(arguments.Skip(verbIndex + 1));
        }

        return safe;
    }

    /// <summary>
    /// Funnels redacted output frames into full-text retention. The runner
    /// already bounds total frames by the output limit and redacts before
    /// emitting, so the spool inherits both properties without new trust.
    /// </summary>
    private static TerminalOutputDeltaHandler? WrapRetain(Action<string>? retainOutput, TerminalOutputDeltaHandler? output) =>
        retainOutput is null ? output : (stream, sequence, text, redacted, token) =>
        {
            retainOutput(text);
            return output?.Invoke(stream, sequence, text, redacted, token) ?? ValueTask.CompletedTask;
        };

    private void EnforceWorkingDirectoryPolicy(CanonicalCommandIntent intent, bool confineWorkspace)
    {
        if (confineWorkspace && !intent.IsInsideWorkspace)
        {
            throw new UnauthorizedAccessException("workspace_path_outside_root");
        }
    }

    private static void ValidateWriteSet(bool readOnly, IReadOnlyList<string>? writeSet)
    {
        try
        {
            WorkspaceWriteSet.Validate(readOnly, writeSet ?? Array.Empty<string>());
        }
        catch (AgentProtocolException exception)
        {
            throw new UnauthorizedAccessException(exception.Code, exception);
        }
    }

    private void EnsureShellApprovalTokenValid(TerminalShellRequest request)
    {
        if (request.Approved)
        {
            return;
        }

        CanonicalCommandIntent intent;
        try
        {
            intent = CanonicalizeShell(request);
        }
        catch (CommandIntentException error)
        {
            throw new UnauthorizedAccessException(error.Code, error);
        }

        if (_approvalTokens is null
            || string.IsNullOrWhiteSpace(request.RequestId)
            || string.IsNullOrWhiteSpace(request.SessionId)
            || !_approvalTokens.TryConsume(
                request.ApprovalToken,
                new ApprovalBinding(
                    request.RequestId,
                    request.SessionId,
                    "powershell",
                    "1.0.0",
                    intent.IntentHash,
                    DateTimeOffset.UtcNow),
                out _))
        {
            throw new UnauthorizedAccessException("terminal_shell_approval_invalid");
        }
    }
}
