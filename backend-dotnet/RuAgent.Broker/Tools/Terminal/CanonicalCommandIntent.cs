using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Terminal;

public sealed class CommandIntentException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// A bounded terminal.run request before canonical resolution.
/// </summary>
public sealed record CanonicalCommandRequest(
    string Executable,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    int? TimeoutMs = null,
    bool NetworkIntent = false);

/// <summary>
/// A bounded terminal.shell request; only explicit PowerShell runtimes are legal.
/// </summary>
public sealed record CanonicalShellRequest(
    string Script,
    string? WorkingDirectory = null,
    int? TimeoutMs = null,
    bool NetworkIntent = false,
    string? ShellRuntime = null);

/// <summary>
/// The immutable, fully resolved execution identity for one terminal call.
/// The intent hash binds resolved executable, argv, canonical cwd, operation class,
/// network intent, and timeout; any changed field produces a different hash so
/// approvals bound to an older hash can never be replayed against it.
/// </summary>
public sealed record CanonicalCommandIntent(
    string ResolvedExecutable,
    IReadOnlyList<string> Arguments,
    string CanonicalWorkingDirectory,
    PathClassification WorkingDirectoryClass,
    bool NetworkIntent,
    int TimeoutMs,
    TerminalOperationClass OperationClass,
    TerminalTimeoutClass TimeoutClass,
    string IntentHash)
{
    public bool IsInsideWorkspace => WorkingDirectoryClass == PathClassification.InsideWorkspace;

    public bool IsProtected => WorkingDirectoryClass == PathClassification.Protected;

    public CanonicalCommandIntent Classified(TerminalOperationClass operationClass)
    {
        if (operationClass == OperationClass)
        {
            return this;
        }

        return this with
        {
            OperationClass = operationClass,
            IntentHash = ComputeIntentHash(
                ResolvedExecutable,
                Arguments,
                CanonicalWorkingDirectory,
                NetworkIntent,
                TimeoutMs,
                operationClass),
        };
    }

    internal static string ComputeIntentHash(
        string resolvedExecutable,
        IReadOnlyList<string> arguments,
        string canonicalWorkingDirectory,
        bool networkIntent,
        int timeoutMs,
        TerminalOperationClass operationClass)
    {
        return TerminalIntentFingerprint.Compute(
            "broker-terminal-intent",
            resolvedExecutable,
            arguments,
            canonicalWorkingDirectory,
            networkIntent,
            timeoutMs,
            operationClass);
    }
}

/// <summary>
/// Resolves an existing path to its final filesystem identity through junctions and
/// symlinks on Windows; other platforms return the lexical full path.
/// </summary>
internal static class FinalPathResolver
{
    private const uint QueryOnlyDesiredAccess = 0;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 0x3;
    private const uint BackupSemanticsFlag = 0x02000000;

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        System.Text.StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    internal static string Resolve(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('\0'))
        {
            throw new UnauthorizedAccessException("terminal_cwd_not_allowed");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch
        {
            throw new UnauthorizedAccessException("terminal_cwd_not_allowed");
        }

        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        using var handle = CreateFile(
            ExtendedDevicePath(fullPath),
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

        var buffer = new System.Text.StringBuilder(1_024);
        var required = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (required > (uint)buffer.Capacity)
        {
            buffer = new System.Text.StringBuilder(checked((int)required));
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
}
