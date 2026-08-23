using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RuAgent.Broker.Policy;

namespace RuAgent.Broker.Tools.Terminal;

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
        PolicyGrant grant,
        TerminalRunRequest request,
        bool confineWorkspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(request);
        if (!AllowedExecutables.Contains(request.Executable)
            || request.Arguments.Count > 64
            || request.Arguments.Any(argument => argument.Length > 1024))
        {
            throw new UnauthorizedAccessException("terminal_command_not_allowed");
        }

        if (request.Network && !grant.Scopes.Contains(ToolScope.TerminalNetwork))
        {
            throw new UnauthorizedAccessException("terminal_network_requires_approval");
        }

        var workingDirectory = ResolveWorkingDirectory(request.RelativeWorkingDirectory, confineWorkspace);
        var environment = SafeEnvironment();
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

    private string ResolveWorkingDirectory(string relativeWorkingDirectory, bool confineWorkspace)
    {
        if (confineWorkspace)
        {
            return _workspace.Resolve(relativeWorkingDirectory);
        }

        if (string.IsNullOrWhiteSpace(relativeWorkingDirectory))
        {
            return _workspace.Root;
        }

        if (relativeWorkingDirectory.Contains('\0'))
        {
            throw new UnauthorizedAccessException("workspace_path_invalid");
        }

        var candidate = relativeWorkingDirectory.Equals(".", StringComparison.Ordinal)
            ? _workspace.Root
            : Path.IsPathRooted(relativeWorkingDirectory)
                ? Path.GetFullPath(relativeWorkingDirectory)
                : Path.GetFullPath(Path.Combine(_workspace.Root, relativeWorkingDirectory));
        if (!Directory.Exists(candidate))
        {
            throw new FileNotFoundException("terminal_cwd_missing", candidate);
        }

        EnsureNotProtectedLocation(ResolveFinalPath(candidate));
        return candidate;
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

    private static void EnsureNotProtectedLocation(string candidate)
    {
        foreach (var protectedLocation in ProtectedLocations())
        {
            var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedLocation));
            if (candidate.Equals(baseDirectory, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(baseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("terminal_cwd_not_allowed");
            }
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
