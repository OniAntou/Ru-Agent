using System.Collections.Frozen;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Policy.Commands;
using RuAgent.Broker.Tools.Git;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// The v2 terminal executor. Every call is canonicalized into a <see cref="CanonicalCommandIntent"/>
/// whose hash binds the resolved executable, argv, canonical working directory, operation class,
/// network intent, and timeout. Recognized read-only/test/build/lint forms run directly; every other
/// form requires a one-time approval token bound to that exact intent hash, so any change between
/// approval and execution invalidates the authorization. Execution streams through the
/// <see cref="StreamingProcessRunner"/> which owns one Job Object per call.
/// </summary>
public sealed class TerminalAdapter
{
    private static readonly FrozenSet<string> RecognizedSafeClasses = new HashSet<string>(
        [
            nameof(TerminalOperationClass.ReadOnly),
            nameof(TerminalOperationClass.Test),
            nameof(TerminalOperationClass.Build),
            nameof(TerminalOperationClass.Lint),
        ],
        StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal);

    private readonly WorkspacePathGuard _workspace;
    private readonly ShellRuntimeResolver _shells;
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
        ShellRuntimeResolver shells,
        ApprovalTokenService? approvalTokens = null,
        AgentCommandEnvironment? commandEnvironment = null,
        StreamingProcessRunner? runner = null,
        CommandIntentResolver? intentResolver = null,
        CommandAnalyzerRegistry? analyzers = null,
        ManagedCommitService? managedCommit = null,
        IGitCli? gitCli = null)
    {
        _workspace = workspace;
        _shells = shells;
        _approvalTokens = approvalTokens;
        _environment = commandEnvironment ?? new AgentCommandEnvironment();
        _runner = runner ?? new StreamingProcessRunner(
            _environment.GetRedactionSeeds(),
            256 * 1024);
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
                request.TimeoutMs));
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(request);
        ValidateWriteSet(request.ReadOnly, request.WriteSet);
        var intent = CanonicalizeRun(request);
        EnforceWorkingDirectoryPolicy(intent, confineWorkspace);

        if (request.Network && !grant.Scopes.Contains(ToolScope.TerminalNetwork))
        {
            throw new UnauthorizedAccessException("terminal_network_requires_approval");
        }

        if (!RecognizedSafeClasses.Contains(intent.OperationClass.ToString()))
        {
            if (request.Approved)
            {
                // V2 authorization was already validated and consumed by the Broker connection.
            }
            else
            {
                // Unrecognized forms execute only with a one-time token bound to this exact intent.
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
                            "terminal.run",
                            "1.0.0",
                            intent.IntentHash,
                            DateTimeOffset.UtcNow),
                        out _))
                {
                    throw new UnauthorizedAccessException("terminal_approval_invalid");
                }
            }
        }

        return ExecuteIntentAsync(intent, cancellationToken);
    }

    public async Task<StreamingProcessResult> RunApprovedShellAsync(
        TerminalShellRequest request,
        bool confineWorkspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateWriteSet(request.ReadOnly, request.WriteSet);
        if (string.IsNullOrWhiteSpace(request.Script) || request.Script.Length > TerminalLimits.MaxScriptChars)
        {
            throw new ArgumentException("terminal_script_invalid", nameof(request));
        }

        var intent = CanonicalizeShell(request);
        EnforceWorkingDirectoryPolicy(intent, confineWorkspace);

        if (confineWorkspace)
        {
            EnsureShellApprovalTokenValid(request);
        }

        var shell = _shells.Resolve(request.Shell);
        return await _runner.RunAsync(
            new ProcessRunSpec(
                intent.ResolvedExecutable,
                shell.PrefixArguments.Concat(intent.Arguments).ToArray(),
                intent.CanonicalWorkingDirectory,
                _environment.ChildEnvironment,
                intent.TimeoutMs,
                256 * 1024),
            onDelta: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StreamingProcessResult> ExecuteIntentAsync(
        CanonicalCommandIntent intent,
        CancellationToken cancellationToken)
    {
        if (ManagedCommitService.IsManagedCommitInvocation(intent.ResolvedExecutable, intent.Arguments))
        {
            return await ExecuteManagedCommitAsync(intent, cancellationToken).ConfigureAwait(false);
        }

        return await _runner.RunAsync(
            new ProcessRunSpec(
                intent.ResolvedExecutable,
                intent.Arguments,
                intent.CanonicalWorkingDirectory,
                _environment.ChildEnvironment,
                intent.TimeoutMs,
                256 * 1024),
            onDelta: null,
            cancellationToken).ConfigureAwait(false);
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
        if (location.Resolution != GitRepositoryResolution.Found || string.IsNullOrEmpty(location.RepositoryRoot))
        {
            var err = "error: not a git repository (or any of the parent directories)";
            return new StreamingProcessResult(
                "failed", 1, 0, string.Empty, string.Empty, err, err, 0, err.Length, 0, 0, 1, 0, intent.CanonicalWorkingDirectory);
        }

        var repoRoot = location.RepositoryRoot;

        // Check git status to find modified/untracked files and verify real index
        var statusResult = await _gitCli.RunAsync(
            new[] { "status", "--porcelain=v2", "--branch" },
            repoRoot,
            cancellationToken).ConfigureAwait(false);

        if (!statusResult.Succeeded)
        {
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

    private void EnforceWorkingDirectoryPolicy(CanonicalCommandIntent intent, bool confineWorkspace)
    {
        if (confineWorkspace && !intent.IsInsideWorkspace)
        {
            throw new UnauthorizedAccessException("workspace_path_outside_root");
        }

        if (!confineWorkspace && IsProtectedLocation(intent.CanonicalWorkingDirectory))
        {
            throw new UnauthorizedAccessException("terminal_cwd_not_allowed");
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

        if (_approvalTokens is null
            || string.IsNullOrWhiteSpace(request.RequestId)
            || string.IsNullOrWhiteSpace(request.SessionId)
            || string.IsNullOrWhiteSpace(request.ArgumentsHash)
            || !_approvalTokens.TryConsume(
                request.ApprovalToken,
                new ApprovalBinding(
                    request.RequestId,
                    request.SessionId,
                    "terminal.shell",
                    "1.0.0",
                    request.ArgumentsHash,
                    DateTimeOffset.UtcNow),
                out _))
        {
            throw new UnauthorizedAccessException("terminal_shell_approval_invalid");
        }
    }

    internal bool IsProtectedLocation(string candidate)
    {
        if (!Directory.Exists(candidate))
        {
            return true;
        }

        try
        {
            var finalPath = FinalPathResolver.Resolve(candidate);
            foreach (var protectedLocation in ProtectedLocations())
            {
                var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedLocation));
                if (finalPath.Equals(baseDirectory, StringComparison.OrdinalIgnoreCase)
                    || finalPath.StartsWith(baseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static IEnumerable<string> ProtectedLocations()
    {
        yield return ProtectedLocation("WINDIR", Environment.SpecialFolder.Windows);
        yield return ProtectedLocation("ProgramFiles", Environment.SpecialFolder.ProgramFiles);
    }

    private static string ProtectedLocation(string variable, Environment.SpecialFolder fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? Environment.GetFolderPath(fallback) : value;
    }
}
