using System.Collections.Frozen;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// Resolves bounded terminal requests into canonical command intents.
/// Structural resolution uses explicit argv preservation; nothing is ever joined
/// into shell text. Executable search runs only through a sanitized absolute
/// directory list, never the raw environment PATH, and model input can never
/// add or override search directories. The network intent is declarative audit
/// metadata only; this class makes no promise that sockets are restricted.
/// </summary>
public sealed class CommandIntentResolver
{
    private static readonly FrozenDictionary<string, string> DeniedArgumentTokens =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--tty"] = "interactive_flag_rejected",
            ["--interactive"] = "interactive_flag_rejected",
            ["/interactive"] = "interactive_flag_rejected",
            ["sudo"] = "password_prompt_rejected",
            ["su"] = "password_prompt_rejected",
            ["runas"] = "password_prompt_rejected",
            ["--ask-pass"] = "password_prompt_rejected",
            ["--password"] = "password_prompt_rejected",
            ["--prompt"] = "password_prompt_rejected",
            ["--daemon"] = "background_daemon_rejected",
            ["--detach"] = "background_daemon_rejected",
            ["-daemon"] = "background_daemon_rejected",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ShellRuntimeAllowlist =
        new[] { "powershell", "pwsh" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] WindowsExecutableExtensions = [".com", ".exe", ".bat", ".cmd"];

    private readonly CanonicalPathResolver _paths;
    private readonly IReadOnlyList<string> _searchDirectories;

    public CommandIntentResolver(string workspaceRoot, IReadOnlyList<string>? sanitizedSearchDirectories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _paths = new CanonicalPathResolver(workspaceRoot);
        _searchDirectories = (sanitizedSearchDirectories ?? SanitizePathFromEnvironment())
            .Where(directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CanonicalWorkspaceRoot = _paths.CanonicalRoot;
    }

    public string CanonicalWorkspaceRoot { get; }

    public CanonicalCommandIntent ResolveRun(CanonicalCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = request.Arguments ?? [];
        ValidateExecutableText(request.Executable);
        ValidateArguments(arguments);
        var timeoutMs = ValidateTimeout(request.TimeoutMs);

        var workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory);
        var resolvedExecutable = ResolveExecutable(request.Executable, workingDirectory.CanonicalPath);
        return BuildIntent(
            resolvedExecutable,
            arguments,
            workingDirectory,
            request.NetworkIntent,
            timeoutMs);
    }

    public CanonicalCommandIntent ResolveShell(CanonicalShellRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Script))
        {
            throw new CommandIntentException("script_invalid");
        }

        if (request.Script.Length > TerminalLimits.MaxScriptChars)
        {
            throw new CommandIntentException("script_too_long");
        }

        if (request.Script.Contains('\0'))
        {
            throw new CommandIntentException("script_nul_rejected");
        }

        var runtime = request.ShellRuntime ?? "powershell";
        if (!ShellRuntimeAllowlist.Contains(runtime))
        {
            throw new CommandIntentException("unsupported_shell_runtime");
        }

        var timeoutMs = ValidateTimeout(request.TimeoutMs);
        var workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory);
        var resolvedExecutable = ResolvePowerShellExecutable(runtime);
        return BuildIntent(
            resolvedExecutable,
            [request.Script],
            workingDirectory,
            request.NetworkIntent,
            timeoutMs);
    }

    private CanonicalCommandIntent BuildIntent(
        string resolvedExecutable,
        IReadOnlyList<string> arguments,
        ResolvedPath workingDirectory,
        bool networkIntent,
        int timeoutMs)
    {
        var timeoutClass = timeoutMs <= TerminalLimits.StandardApprovalThresholdMs
            ? TerminalTimeoutClass.StandardWindow
            : TerminalTimeoutClass.ApprovalRequired;
        var hash = CanonicalCommandIntent.ComputeIntentHash(
            resolvedExecutable,
            arguments,
            workingDirectory.CanonicalPath,
            networkIntent,
            timeoutMs,
            TerminalOperationClass.Unknown);
        return new CanonicalCommandIntent(
            resolvedExecutable,
            arguments.ToArray(),
            workingDirectory.CanonicalPath,
            workingDirectory.Classification,
            networkIntent,
            timeoutMs,
            TerminalOperationClass.Unknown,
            timeoutClass,
            hash);
    }

    private static void ValidateExecutableText(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new CommandIntentException("executable_invalid");
        }

        if (executable.Length > TerminalLimits.MaxExecutableChars || executable.Contains('\0'))
        {
            throw new CommandIntentException("executable_invalid");
        }
    }

    private static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Count > TerminalLimits.MaxArguments)
        {
            throw new CommandIntentException("argv_too_long");
        }

        foreach (var argument in arguments)
        {
            if (argument.Contains('\0'))
            {
                throw new CommandIntentException("argument_nul_rejected");
            }

            if (argument.Length > TerminalLimits.MaxArgumentChars)
            {
                throw new CommandIntentException("argument_too_long");
            }
        }

        foreach (var argument in arguments)
        {
            if (DeniedArgumentTokens.TryGetValue(argument, out var code))
            {
                throw new CommandIntentException(code);
            }
        }
    }

    private static int ValidateTimeout(int? timeoutMs)
    {
        var effective = timeoutMs ?? TerminalLimits.DefaultTimeoutMs;
        if (effective < 1)
        {
            throw new CommandIntentException("timeout_invalid");
        }

        if (effective > TerminalLimits.HardMaxTimeoutMs)
        {
            throw new CommandIntentException("timeout_exceeds_hard_max");
        }

        return effective;
    }

    private ResolvedPath ResolveWorkingDirectory(string? requestedWorkingDirectory)
    {
        try
        {
            var resolved = _paths.Resolve(requestedWorkingDirectory ?? ".");
            if (!Directory.Exists(resolved.CanonicalPath))
            {
                throw new CommandIntentException("working_directory_invalid");
            }

            return resolved;
        }
        catch (Exception ex) when (ex is not CommandIntentException)
        {
            throw new CommandIntentException("working_directory_invalid");
        }
    }

    private string ResolveExecutable(string executable, string canonicalWorkingDirectory)
    {
        if (Path.IsPathRooted(executable))
        {
            if (!File.Exists(executable))
            {
                throw new CommandIntentException("executable_not_found");
            }

            return Path.GetFullPath(executable);
        }

        if (executable.Contains('/') || executable.Contains('\\'))
        {
            var combined = Path.GetFullPath(Path.Combine(canonicalWorkingDirectory, executable));
            if (!File.Exists(combined))
            {
                throw new CommandIntentException("executable_not_found");
            }

            return combined;
        }

        var hasWindowsExtension = WindowsExecutableExtensions.Contains(
            Path.GetExtension(executable), StringComparer.OrdinalIgnoreCase);
        foreach (var directory in _searchDirectories)
        {
            if (hasWindowsExtension)
            {
                var directCandidate = Path.Combine(directory, executable);
                if (File.Exists(directCandidate))
                {
                    return Path.GetFullPath(directCandidate);
                }

                continue;
            }

            foreach (var extension in WindowsExecutableExtensions)
            {
                var candidate = Path.Combine(directory, executable + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        throw new CommandIntentException("executable_not_found");
    }

    private string ResolvePowerShellExecutable(string runtime)
    {
        var executableName = runtime.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            ? "pwsh.exe"
            : "powershell.exe";
        foreach (var directory in _searchDirectories)
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var fallback = runtime.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("ProgramFiles")
            : Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            var knownPath = runtime.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(fallback, "PowerShell", "7", executableName)
                : Path.Combine(fallback, "WindowsPowerShell", "v1.0", executableName);
            if (File.Exists(knownPath))
            {
                return Path.GetFullPath(knownPath);
            }
        }

        throw new CommandIntentException("executable_not_found");
    }

    private static IReadOnlyList<string> SanitizePathFromEnvironment()
    {
        var rawPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return rawPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathRooted)
            .ToArray();
    }
}
