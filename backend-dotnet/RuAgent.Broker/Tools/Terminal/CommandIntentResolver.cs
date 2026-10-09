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
            ["/prompt"] = "password_prompt_rejected",
            ["doas"] = "password_prompt_rejected",
            ["pkexec"] = "password_prompt_rejected",
            ["--daemon"] = "background_daemon_rejected",
            ["--detach"] = "background_daemon_rejected",
            ["-daemon"] = "background_daemon_rejected",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ShellRuntimeAllowlist =
        new[] { "powershell", "pwsh" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // The fixed invocation prefix for both allowlisted runtimes. It is part of
    // the canonical argv so the approval-bound intent hash covers the exact
    // command line that will execute.
    private static readonly string[] ShellPrefixArguments = ["-NoProfile", "-NonInteractive", "-Command"];

    private static readonly string[] WindowsExecutableExtensions = [".com", ".exe", ".bat", ".cmd"];

    /// <summary>
    /// Operator-supplied additional trusted executable directories, semicolon
    /// separated, on top of <see cref="TrustedExecutableDirectories"/>.
    /// </summary>
    public const string TrustedDirectoriesVariable = "RU_TRUSTED_EXECUTABLE_DIRS";

    /// <summary>
    /// Default trusted executable directories. A bare executable name is resolved
    /// through the sanitized PATH list, and the file that actually runs must live
    /// in one of these directories.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists: policy classifies a bare name (<c>git</c>) using the token the
    /// model supplied, while resolution picks the first match on PATH. Model-side
    /// arbitrary code execution is already available through auto-executable build and
    /// test commands, so a program written into a per-user PATH directory
    /// (<c>%USERPROFILE%\.dotnet\tools</c>, scoop shims, portable toolchains) would
    /// then run under a command identity policy treats as permanently safe — never
    /// surfacing an approval, and escaping workspace attribution. Naming the trusted
    /// directories inverts the check the other way: instead of guessing which
    /// directories are writable, it states which ones are trusted.
    /// </para>
    /// <para>
    /// Per-user interpreter and tool directories are deliberately NOT trusted, because
    /// they are exactly the vector. An operator with a legitimate toolchain in a
    /// per-user directory adds it through <see cref="TrustedDirectoriesVariable"/>.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> TrustedExecutableDirectories { get; } =
        BuildTrustedExecutableDirectories();

    private readonly CanonicalPathResolver _paths;
    private readonly IReadOnlyList<string> _searchDirectories;
    private readonly IReadOnlyList<string> _trustedDirectories;

    public CommandIntentResolver(
        string workspaceRoot,
        IReadOnlyList<string>? sanitizedSearchDirectories = null,
        IReadOnlyList<string>? trustedExecutableDirectories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _paths = new CanonicalPathResolver(workspaceRoot);
        _searchDirectories = (sanitizedSearchDirectories ?? SanitizePathFromEnvironment())
            .Where(directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _trustedDirectories = NormalizeDirectories(
            trustedExecutableDirectories ?? TrustedExecutableDirectories);
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
            [.. ShellPrefixArguments, request.Script],
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
            TimeoutClassFor(timeoutMs),
            hash);
    }

    /// <summary>
    /// The timeout class the protocol defines: at or below the standard approval
    /// threshold the command decision stands, above it an explicit approval is
    /// required before execution. The hard maximum is absolute and rejects earlier.
    /// </summary>
    private static TerminalTimeoutClass TimeoutClassFor(int timeoutMs) =>
        timeoutMs > TerminalLimits.StandardApprovalThresholdMs
            ? TerminalTimeoutClass.ApprovalRequired
            : TerminalTimeoutClass.StandardWindow;

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

            return CanonicalizeExistingFile(executable);
        }

        if (executable.Contains('/') || executable.Contains('\\'))
        {
            var combined = Path.GetFullPath(Path.Combine(canonicalWorkingDirectory, executable));
            if (!File.Exists(combined))
            {
                throw new CommandIntentException("executable_not_found");
            }

            return CanonicalizeExistingFile(combined);
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
                    return RequireTrustedExecutable(CanonicalizeExistingFile(directCandidate));
                }

                continue;
            }

            foreach (var extension in WindowsExecutableExtensions)
            {
                var candidate = Path.Combine(directory, executable + extension);
                if (File.Exists(candidate))
                {
                    return RequireTrustedExecutable(CanonicalizeExistingFile(candidate));
                }
            }
        }

        throw new CommandIntentException("executable_not_found");
    }

    /// <summary>
    /// A bare executable name is matched on PATH, so the file that runs is chosen by
    /// directory order rather than by the policy decision, which only ever saw the
    /// name. Trusting the containing directory is what keeps that choice honest.
    /// Path-qualified and rooted requests do not come through here: policy already
    /// requires an approval for them, so their identity is confirmed by a human.
    /// </summary>
    private string RequireTrustedExecutable(string resolvedPath)
    {
        var directory = Path.GetDirectoryName(resolvedPath);
        if (directory is null)
        {
            throw new CommandIntentException("executable_not_trusted");
        }

        var normalized = NormalizeDirectory(directory);
        foreach (var trusted in _trustedDirectories)
        {
            if (string.Equals(trusted, normalized, StringComparison.OrdinalIgnoreCase)
                || IsUnderTrustedRoot(normalized, trusted))
            {
                return resolvedPath;
            }
        }

        throw new CommandIntentException("executable_not_trusted");
    }

    /// <summary>
    /// A machine-wide install puts its binaries in a subdirectory of a trusted root
    /// (<c>Program Files\Git\cmd</c>, <c>Program Files\dotnet</c>). Those are not
    /// user-writable, so the subtree is trusted with the root. Per-user directories
    /// never match: they are the vector, and an operator with a legitimate per-user
    /// toolchain names it through <see cref="TrustedDirectoriesVariable"/>.
    /// </summary>
    private static bool IsUnderTrustedRoot(string normalized, string trustedRoot) =>
        normalized.Length > trustedRoot.Length
        && normalized.StartsWith(trustedRoot, StringComparison.OrdinalIgnoreCase)
        && (normalized[trustedRoot.Length] == '\\' || normalized[trustedRoot.Length] == '/');

    private static IReadOnlyList<string> BuildTrustedExecutableDirectories()
    {
        var directories = new List<string>();
        void Add(Environment.SpecialFolder folder)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path))
            {
                directories.Add(path);
            }
        }

        Add(Environment.SpecialFolder.System);
        Add(Environment.SpecialFolder.SystemX86);
        Add(Environment.SpecialFolder.Windows);
        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);
        Add(Environment.SpecialFolder.CommonProgramFiles);
        Add(Environment.SpecialFolder.CommonProgramFilesX86);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        directories.Add(Path.Combine(programFiles, "dotnet"));
        directories.Add(Path.Combine(programFiles, "nodejs"));
        directories.Add(Path.Combine(roamingAppData, "npm"));
        directories.Add(Path.Combine(localAppData, "Programs", "Python"));

        var overrideValue = Environment.GetEnvironmentVariable(TrustedDirectoriesVariable);
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            directories.AddRange(overrideValue
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return NormalizeDirectories(directories);
    }

    private static IReadOnlyList<string> NormalizeDirectories(IEnumerable<string> directories)
    {
        var normalized = new List<string>();
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                normalized.Add(NormalizeDirectory(Path.GetFullPath(directory)));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An unusable entry cannot be trusted or searched; skipping it keeps
                // the rest of the list usable instead of failing every resolution.
            }
        }

        return normalized
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeDirectory(string directory) =>
        directory.TrimEnd('\\', '/');

    /// <summary>
    /// The resolved identity is the final file target: an executable reached through a
    /// junction or symlink binds to the real file it runs, never to the link path.
    /// </summary>
    private static string CanonicalizeExistingFile(string candidate) => FinalPathResolver.Resolve(candidate);

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
                return RequireTrustedExecutable(CanonicalizeExistingFile(candidate));
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
                return RequireTrustedExecutable(CanonicalizeExistingFile(knownPath));
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
