using RuAgent.Broker.Policy;
using RuAgent.Broker.Policy.Commands;
using RuAgent.Broker.Tools.Git;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// Single source of truth for which terminal operation classes execute without
/// approval. PolicySnapshotEvaluator consumes this verdict for the evaluate leg;
/// this adapter enforces the same verdict for the execution leg. Read-only, test,
/// build, lint, and local-git run payloads execute on an authorization token;
/// external mutation, delete, shell scripts, and unknown always ask for a
/// one-time approval token bound to the exact intent hash. The local-git class
/// only covers clean repository writes: `git config` mutations, `git -C`
/// overrides, and program-selecting git options are analyzed as unknown forms
/// and ask despite their verb.
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
/// network intent, and timeout. Auto forms (see <see cref="TerminalAutoPolicy"/>) run directly;
/// every other form requires a one-time approval token bound to that exact intent hash, so any
/// change between approval and execution invalidates the authorization. Execution streams through
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
    private readonly IGitCli _gitCli;
    private readonly ManagedCommitService _managedCommit;
    private readonly GitRepositoryLocator _gitLocator;

    public TerminalAdapter(
        WorkspacePathGuard workspace,
        ApprovalTokenService? approvalTokens = null,
        AgentCommandEnvironment? commandEnvironment = null,
        StreamingProcessRunner? runner = null,
        CommandIntentResolver? intentResolver = null,
        CommandAnalyzerRegistry? analyzers = null,
        ManagedCommitService? managedCommit = null,
        IGitCli? gitCli = null)
    {
        _workspace = workspace;
        _approvalTokens = approvalTokens;
        _environment = commandEnvironment ?? new AgentCommandEnvironment();
        _runner = runner ?? new StreamingProcessRunner(
            _environment.GetRedactionSeeds(),
            TerminalLimits.MaxSpoolFileBytes);
        _resolver = intentResolver ?? new CommandIntentResolver(_workspace.Root);
        _analyzers = analyzers ?? new CommandAnalyzerRegistry();
        _gitCli = gitCli ?? new StreamingGitCli(_runner, _environment.ChildEnvironment);
        _managedCommit = managedCommit ?? new ManagedCommitService(_gitCli);
        _gitLocator = new GitRepositoryLocator(_gitCli);
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
        int? killTimeoutMs = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(request);
        ValidateWriteSet(request.ReadOnly, request.WriteSet);
        var intent = CanonicalizeRun(request);
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
                    || (!_approvalTokens.TryConsume(
                        request.ApprovalToken,
                        new ApprovalBinding(
                            request.RequestId,
                            request.SessionId,
                            "terminal.run",
                            "1.0.0",
                            intent.IntentHash,
                            DateTimeOffset.UtcNow),
                        out _)
                        && !_approvalTokens.TryConsume(
                        request.ApprovalToken,
                        new ApprovalBinding(
                            request.RequestId,
                            request.SessionId,
                            "bash",
                            "1.0.0",
                            intent.IntentHash,
                            DateTimeOffset.UtcNow),
                        out _)))
                {
                    throw new UnauthorizedAccessException("terminal_approval_invalid");
                }
            }
        }

        return ExecuteIntentAsync(
            intent,
            cancellationToken,
            retainOutput,
            killTimeoutMs);
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
        PolicyGrant? grant = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateWriteSet(request.ReadOnly, request.WriteSet);
        if (string.IsNullOrWhiteSpace(request.Script) || request.Script.Length > TerminalLimits.MaxScriptChars)
        {
            throw new ArgumentException("terminal_script_invalid", nameof(request));
        }

        var intent = CanonicalizeShell(request);
        EnforceWorkingDirectoryPolicy(intent, confineWorkspace);

        // Shell scripts always require explicit approval through the same
        // one-time token path as terminal.run: the evaluator asks (never auto)
        // and V2 authorization arrives via Approved/token. Working-directory checks apply before execution.
        EnsureShellApprovalTokenValid(request);

        return await _runner.RunAsync(
            new ProcessRunSpec(
                intent.ResolvedExecutable,
                intent.Arguments,
                intent.CanonicalWorkingDirectory,
                _environment.ChildEnvironment,
                killTimeoutMs ?? intent.TimeoutMs,
                TerminalLimits.MaxSpoolFileBytes),
            WrapRetain(retainOutput),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StreamingProcessResult> ExecuteIntentAsync(
        CanonicalCommandIntent intent,
        CancellationToken cancellationToken,
        Action<string>? retainOutput = null,
        int? killTimeoutMs = null)
    {
        if (ManagedCommitService.IsManagedCommitInvocation(intent.ResolvedExecutable, intent.Arguments))
        {
            return await ExecuteManagedCommitAsync(intent, cancellationToken).ConfigureAwait(false);
        }

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
            WrapRetain(retainOutput),
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
    private static TerminalOutputDeltaHandler? WrapRetain(Action<string>? retainOutput)
    {
        if (retainOutput is null)
        {
            return null;
        }

        return (stream, sequence, text, redacted, emitToken) =>
        {
            retainOutput(text);
            return ValueTask.CompletedTask;
        };
    }

    private async Task<StreamingProcessResult> ExecuteManagedCommitAsync(
        CanonicalCommandIntent intent,
        CancellationToken cancellationToken)
    {
        var message = ManagedCommitService.TryExtractCommitMessage(intent.Arguments);
        if (string.IsNullOrWhiteSpace(message))
        {
            var err = "error: commit message must be provided using -m or --message";
            return new StreamingProcessResult(
                "failed", 1, 0, string.Empty, string.Empty, err, err, 0, err.Length, 0, 0, 1, 0, intent.CanonicalWorkingDirectory);
        }

        var location = await _gitLocator.LocateAsync(intent.CanonicalWorkingDirectory, cancellationToken).ConfigureAwait(false);
        if (location.Resolution == GitRepositoryResolution.NoRepository)
        {
            var err = "error: not a git repository (or any of the parent directories)";
            return new StreamingProcessResult(
                "failed", 1, 0, string.Empty, string.Empty, err, err, 0, err.Length, 0, 0, 1, 0, intent.CanonicalWorkingDirectory);
        }

        if (location.Resolution != GitRepositoryResolution.Found || string.IsNullOrEmpty(location.RepositoryRoot))
        {
            return ManagedCommitFailure(intent, location.StderrTail);
        }

        var repoRoot = location.RepositoryRoot;

        // Check git status to find modified/untracked files and verify real index
        var statusResult = await _gitCli.RunAsync(
            new[] { "status", "--porcelain=v2", "--branch" },
            repoRoot,
            cancellationToken).ConfigureAwait(false);

        if (!statusResult.Succeeded)
        {
            if (statusResult.ExitCode < 0)
            {
                return ManagedCommitFailure(intent, statusResult.FailureReason);
            }

            var err = "error: unable to read git status before commit: " + statusResult.Stderr;
            return new StreamingProcessResult(
                "failed", 1, 0, string.Empty, string.Empty, err, err, 0, err.Length, 0, 0, 1, 0, intent.CanonicalWorkingDirectory);
        }

        var parsed = GitStatusParser.Parse(statusResult.Stdout);
        var hasStaged = parsed.Entries.Any(e => e.HasStagedChanges);
        if (hasStaged)
        {
            var err = "error: managed commit refused: index_not_empty. Existing staged changes would be overwritten. Please unstage or commit them manually.";
            return new StreamingProcessResult(
                "failed", 1, 0, string.Empty, string.Empty, err, err, 0, err.Length, 0, 0, 1, 0, intent.CanonicalWorkingDirectory);
        }

        var changedPaths = parsed.Entries
            .Where(e => e.HasUnstagedChanges || e.Kind == RunGitEntryKind.Untracked)
            .Select(e => e.Path)
            .ToArray();

        if (changedPaths.Length == 0)
        {
            var err = "error: managed commit refused: nothing_attributed. No modified or untracked files to commit.";
            return new StreamingProcessResult(
                "failed", 1, 0, string.Empty, string.Empty, err, err, 0, err.Length, 0, 0, 1, 0, intent.CanonicalWorkingDirectory);
        }

        var preview = await _managedCommit.BuildPreviewAsync(
            repoRoot,
            changedPaths,
            Array.Empty<string>(),
            cancellationToken).ConfigureAwait(false);

        if (!preview.Succeeded || preview.Plan is null)
        {
            var err = $"error: managed commit preview refused: {preview.RefusalCode}";
            return new StreamingProcessResult(
                "failed", 1, 0, string.Empty, string.Empty, err, err, 0, err.Length, 0, 0, 1, 0, intent.CanonicalWorkingDirectory);
        }

        var outcome = await _managedCommit.ExecuteApprovedAsync(
            preview.Plan,
            message,
            cancellationToken).ConfigureAwait(false);

        if (outcome.Phase == ManagedCommitPhase.Committed)
        {
            var outText = $"[managed commit {outcome.NewHeadOid}] {message}\n";
            return new StreamingProcessResult(
                "succeeded", 0, 50, outText, outText, string.Empty, string.Empty, outText.Length, 0, 0, 0, 1, 0, intent.CanonicalWorkingDirectory);
        }

        var failMsg = outcome.Phase == ManagedCommitPhase.ReconciliationRequired
            ? $"error: managed commit failed with reconciliation required: {outcome.Code}"
            : $"error: managed commit aborted: {outcome.Code}";

        return new StreamingProcessResult(
            "failed", 1, 50, string.Empty, string.Empty, failMsg, failMsg, 0, failMsg.Length, 0, 0, 1, 0, intent.CanonicalWorkingDirectory);
    }

    private static StreamingProcessResult ManagedCommitFailure(CanonicalCommandIntent intent, string failureCode)
    {
        var timedOut = failureCode == "git_timed_out";
        var code = failureCode == "process_start_failed"
            ? failureCode
            : timedOut ? failureCode : "git_command_failed";
        return new StreamingProcessResult(
            timedOut ? "timed_out" : "failed", code == "git_command_failed" ? 1 : null, 0, string.Empty, string.Empty, code, string.Empty,
            0, code.Length, 0, 0, 0, 0, intent.CanonicalWorkingDirectory);
    }

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
            || (!_approvalTokens.TryConsume(
                request.ApprovalToken,
                new ApprovalBinding(
                    request.RequestId,
                    request.SessionId,
                    "terminal.shell",
                    "1.0.0",
                    intent.IntentHash,
                    DateTimeOffset.UtcNow),
                out _)
                && !_approvalTokens.TryConsume(
                request.ApprovalToken,
                new ApprovalBinding(
                    request.RequestId,
                    request.SessionId,
                    "bash",
                    "1.0.0",
                    intent.IntentHash,
                    DateTimeOffset.UtcNow),
                out _)))
        {
            throw new UnauthorizedAccessException("terminal_shell_approval_invalid");
        }
    }
}
