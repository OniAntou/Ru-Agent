using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Policy.Commands;
using RuAgent.Protocol;

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
    private const uint QueryOnlyDesiredAccess = 0;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 0x3;
    private const uint BackupSemanticsFlag = 0x02000000;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

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

    public TerminalAdapter(
        WorkspacePathGuard workspace,
        ShellRuntimeResolver shells,
        ApprovalTokenService? approvalTokens = null,
        AgentCommandEnvironment? commandEnvironment = null,
        StreamingProcessRunner? runner = null,
        CommandIntentResolver? intentResolver = null,
        CommandAnalyzerRegistry? analyzers = null)
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
        var intent = CanonicalizeRun(request);
        EnforceWorkingDirectoryPolicy(intent, confineWorkspace);

        if (request.Network && !grant.Scopes.Contains(ToolScope.TerminalNetwork))
        {
            throw new UnauthorizedAccessException("terminal_network_requires_approval");
        }

        if (!RecognizedSafeClasses.Contains(intent.OperationClass.ToString()))
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

        return ExecuteIntentAsync(intent, cancellationToken);
    }

    public async Task<StreamingProcessResult> RunApprovedShellAsync(
        TerminalShellRequest request,
        bool confineWorkspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
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
        CancellationToken cancellationToken) =>
        await _runner.RunAsync(
            new ProcessRunSpec(
                intent.ResolvedExecutable,
                intent.Arguments,
                intent.CanonicalWorkingDirectory,
                _environment.ChildEnvironment,
                intent.TimeoutMs,
                256 * 1024),
            onDelta: null,
            cancellationToken).ConfigureAwait(false);

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

    private void EnsureShellApprovalTokenValid(TerminalShellRequest request)
    {
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
            var finalPath = ResolveFinalPath(candidate);
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

    private static string ResolveFinalPath(string candidate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(candidate);
        }

        using var handle = CreateFile(
            ExtendedDevicePath(candidate),
            QueryOnlyDesiredAccess,
            FileShareReadWrite,
            IntPtr.Zero,
            OpenExisting,
            BackupSemanticsFlag,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new UnauthorizedAccessException("terminal_cwd_not_allowed");
        }

        var buffer = new StringBuilder(1_024);
        var required = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (required > (uint)buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)required));
            required = GetFinalPathNameByHandle(handle, buffer, required, 0);
        }

        if (required == 0)
        {
            throw new UnauthorizedAccessException("terminal_cwd_not_allowed");
        }

        return StripDevicePrefix(buffer.ToString());
    }

    private static string ExtendedDevicePath(string candidate)
    {
        if (candidate.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        return candidate.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
            ? @"\\?\UNC\" + candidate[2..]
            : @"\\?\" + candidate;
    }

    private static string StripDevicePrefix(string finalPath)
    {
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + finalPath[@"\\?\UNC\".Length..];
        }

        return finalPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? finalPath[@"\\?\".Length..]
            : finalPath;
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
