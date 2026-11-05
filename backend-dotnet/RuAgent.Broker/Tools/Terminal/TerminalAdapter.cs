using RuAgent.Broker.Policy;
using RuAgent.Broker.Policy.Commands;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// The v2 terminal executor. Every call is canonicalized into a <see cref="CanonicalCommandIntent"/>
/// whose hash binds the resolved executable, argv, canonical working directory, operation class,
/// network intent, and timeout. Every launch must carry Broker-validated authorization
/// (<c>Approved</c>): <see cref="Runtime.BrokerExecutionRuntime"/> decides allow/ask from the
/// run's policy snapshot and consumes the single-use token before dispatch, and an
/// unauthorized request is refused here whatever its operation class.
/// Execution streams through
/// the <see cref="StreamingProcessRunner"/> which owns one Job Object per call.
/// </summary>
public sealed class TerminalAdapter
{
    private readonly WorkspacePathGuard _workspace;
    private readonly AgentCommandEnvironment _environment;
    private readonly StreamingProcessRunner _runner;
    private readonly CommandIntentResolver _resolver;
    private readonly CommandAnalyzerRegistry _analyzers;

    public TerminalAdapter(
        WorkspacePathGuard workspace,
        AgentCommandEnvironment? commandEnvironment = null,
        StreamingProcessRunner? runner = null,
        CommandIntentResolver? intentResolver = null,
        CommandAnalyzerRegistry? analyzers = null)
    {
        _workspace = workspace;
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
        CancellationToken cancellationToken,
        Action<string>? retainOutput = null,
        int? killTimeoutMs = null,
        TerminalOutputDeltaHandler? output = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(request);
        ValidateWriteSet(request.ReadOnly, request.WriteSet);
        var intent = request.AuthorizedIntent ?? CanonicalizeRun(request);

        if (request.Network && !IsNetworkAuthorized(grant, request.Network, request.Approved))
        {
            throw new UnauthorizedAccessException("terminal_network_requires_approval");
        }

        // Fail closed: only a Broker-authorized intent launches, whatever its class.
        if (!request.Approved)
        {
            throw new UnauthorizedAccessException("terminal_command_not_allowed");
        }

        return ExecuteIntentAsync(
            intent,
            cancellationToken,
            retainOutput,
            killTimeoutMs, output);
    }

    /// <summary>
    /// The single authorized-network condition: a declared network intent is
    /// authorized by the terminal-network scope or an approved execution.
    /// This is request policy, not OS network isolation.
    /// </summary>
    internal static bool IsNetworkAuthorized(
        PolicyGrant? grant,
        bool networkIntent,
        bool approved) =>
        networkIntent
        && ((grant?.Scopes.Contains(ToolScope.TerminalNetwork) ?? false)
            || approved);

    public async Task<StreamingProcessResult> RunApprovedShellAsync(
        TerminalShellRequest request,
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

        // V2 authorization (automatic or approved) has already been consumed by
        // the Broker; an unauthorized shell request never launches.
        if (!request.Approved)
        {
            throw new UnauthorizedAccessException("terminal_shell_approval_invalid");
        }

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
}
