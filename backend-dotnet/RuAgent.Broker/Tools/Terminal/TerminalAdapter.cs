using System.Collections.Frozen;
using RuAgent.Broker.Policy;

namespace RuAgent.Broker.Tools.Terminal;

public sealed class TerminalAdapter
{
    private static readonly FrozenSet<string> AllowedExecutables = new HashSet<string>(
        ["git", "rg", "dotnet", "pytest", "python", "python.exe"],
        StringComparer.OrdinalIgnoreCase).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private readonly WorkspacePathGuard _workspace;
    private readonly ProcessTreeRunner _runner;
    private readonly ShellRuntimeResolver _shells;
    private readonly ApprovalTokenService? _approvalTokens;

    public TerminalAdapter(
        WorkspacePathGuard workspace,
        ProcessTreeRunner runner,
        ShellRuntimeResolver shells,
        ApprovalTokenService? approvalTokens = null)
    {
        _workspace = workspace;
        _runner = runner;
        _shells = shells;
        _approvalTokens = approvalTokens;
    }

    public Task<ProcessRunResult> RunAsync(
        TerminalRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AllowedExecutables.Contains(request.Executable)
            || request.Arguments.Count > 64
            || request.Arguments.Any(argument => argument.Length > 1024))
        {
            throw new UnauthorizedAccessException("terminal_command_not_allowed");
        }

        var workingDirectory = _workspace.Resolve(request.RelativeWorkingDirectory);
        var environment = SafeEnvironment();
        if (request.Network)
        {
            throw new UnauthorizedAccessException("terminal_network_requires_approval");
        }

        return _runner.RunAsync(
            new ProcessRunSpec(
                request.Executable,
                request.Arguments,
                workingDirectory,
                environment,
                request.TimeoutMs,
                256 * 1024),
            cancellationToken);
    }

    public Task<ProcessRunResult> RunApprovedShellAsync(
        TerminalShellRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Script) || request.Script.Length > 16_000)
        {
            throw new ArgumentException("terminal_script_invalid", nameof(request));
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

        var workingDirectory = _workspace.Resolve(request.RelativeWorkingDirectory);
        var shell = _shells.Resolve(request.Shell);
        var args = shell.PrefixArguments.Concat(
            request.Shell.Equals("wsl", StringComparison.OrdinalIgnoreCase)
                ? [workingDirectory, "--", request.Script]
                : [request.Script]).ToArray();
        return _runner.RunAsync(
            new ProcessRunSpec(
                shell.Executable,
                args,
                workingDirectory,
                SafeEnvironment(),
                request.TimeoutMs,
                256 * 1024),
            cancellationToken);
    }

    private static IReadOnlyDictionary<string, string> SafeEnvironment()
    {
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "TEMP", "TMP" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                allowed[key] = value;
            }
        }

        return allowed;
    }
}
