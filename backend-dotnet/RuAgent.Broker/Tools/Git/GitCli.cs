using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Git;

public sealed record GitCliResult(
    bool Succeeded,
    int ExitCode,
    string Stdout,
    string Stderr,
    string FailureReason);

/// <summary>
/// Bounded, non-interactive execution channel for fixed read-only Git reads. Every
/// invocation runs through the structured streaming process runner with an argv list
/// (never shell-joined text), the sanitized agent child environment, a fixed timeout,
/// and a hard output cap.
/// </summary>
public interface IGitCli
{
    Task<GitCliResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalEnvironment = null);
}

public sealed class StreamingGitCli : IGitCli
{
    private const int TimeoutMs = 30_000;
    private const int DefaultOutputLimitBytes = 256 * 1024;
    private const int MaximumOutputLimitBytes = 8 * 1024 * 1024;
    private const int MaximumFilterDrivers = 64;
    private const int MaximumFilterKeyChars = 512;
    private const int MaximumFilterConfigEntries = 256;

    private static readonly HashSet<string> WriteVerbs = new(StringComparer.Ordinal)
    {
        "add", "am", "apply", "checkout", "cherry-pick", "clean", "commit",
        "commit-tree", "fetch", "filter-branch", "gc", "maintenance", "merge", "mv",
        "notes", "pull", "push", "rebase", "read-tree", "reset", "restore", "revert",
        "rm", "stash", "submodule", "switch", "tag", "update-index", "worktree",
        "write-tree",
    };

    private readonly StreamingProcessRunner _runner;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly int _outputLimitBytes;
    private readonly int _timeoutMs;

    public StreamingGitCli(
        StreamingProcessRunner? runner = null,
        IReadOnlyDictionary<string, string>? environment = null,
        int outputLimitBytes = DefaultOutputLimitBytes,
        int timeoutMs = TimeoutMs)
    {
        if (outputLimitBytes is < 1 or > MaximumOutputLimitBytes)
            throw new ArgumentOutOfRangeException(nameof(outputLimitBytes));
        if (timeoutMs < 1)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        _outputLimitBytes = outputLimitBytes;
        _timeoutMs = timeoutMs;
        // Git status is line-oriented and callers need the whole bounded answer:
        // the runner's 8k+8k default would report a 300-file status as truncated.
        _runner = runner ?? new StreamingProcessRunner(
            outputLimitBytes: outputLimitBytes,
            headCapacityChars: outputLimitBytes,
            tailCapacityChars: 0);
        _environment = environment ?? new AgentCommandEnvironment().ChildEnvironment;
    }

    /// <summary>
    /// Finds the subcommand after any global options (<c>-c name=value</c>,
    /// <c>--git-dir=...</c>, ...). Checking only <c>arguments[0]</c> missed
    /// <c>GitStatusAdapter</c>, whose argv starts with <c>-c</c>.
    /// </summary>
    private static string? GitVerb(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "-c" or "-C" or "--config-env" or "--exec-path" or "--git-dir" or "--namespace" or "--work-tree")
            {
                index++;
                continue;
            }

            if (argument.StartsWith("--config-env=", StringComparison.Ordinal)
                || argument.StartsWith("--exec-path=", StringComparison.Ordinal)
                || argument.StartsWith("--git-dir=", StringComparison.Ordinal)
                || argument.StartsWith("--namespace=", StringComparison.Ordinal)
                || argument.StartsWith("--work-tree=", StringComparison.Ordinal))
            {
                continue;
            }

            if (argument.StartsWith('-'))
            {
                continue;
            }

            return argument;
        }

        return null;
    }

    /// <summary>
    /// Read verbs (and unknown verbs) get filter neutralization. Only an explicit
    /// write verb keeps repository filters, because those filters are part of what
    /// the write must do (LFS, content transforms).
    /// </summary>
    private static bool DisablesRepositoryFilters(IReadOnlyList<string> arguments)
    {
        var verb = GitVerb(arguments);
        return verb is null || !WriteVerbs.Contains(verb);
    }

    /// <summary>
    /// Builds GIT_CONFIG_* environment entries that override every local/worktree
    /// clean/smudge/process filter. Environment config avoids <c>-c</c> parsing
    /// (a driver name may contain <c>=</c>), and local scope only keeps global
    /// filters such as LFS active so a valid clean file is not reported as modified.
    /// Returns null when the table cannot be read safely, so the caller fails closed.
    /// </summary>
    private async Task<Dictionary<string, string>?> FilterEnvironmentAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        foreach (var scope in new[] { "--local", "--worktree" })
        {
            var spec = new ProcessRunSpec(
                "git",
                [
                    "--no-optional-locks",
                    "-c",
                    "core.fsmonitor=false",
                    "config",
                    scope,
                    "--null",
                    "--name-only",
                    "--get-regexp",
                    @"^filter\..*\.(clean|smudge|process)$",
                ],
                workingDirectory,
                _environment,
                _timeoutMs,
                _outputLimitBytes,
                HeadCapacityChars: _outputLimitBytes,
                TailCapacityChars: 0);
            var result = await _runner.RunAsync(spec, onDelta: null, cancellationToken).ConfigureAwait(false);
            if (result.Status == "cancelled")
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (result.Status == "timed_out" || result.ExitCode is null)
            {
                return null;
            }

            // Exit 1 means no filter in this scope; a missing worktree config is
            // also normal. Another failure is left to the main command.
            if (result.ExitCode != 0)
            {
                continue;
            }

            if (result.StdoutDroppedBytes > 0 || result.StderrDroppedBytes > 0)
            {
                return null;
            }

            keys.AddRange(result.StdoutHead.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }

        var drivers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var driver = key switch
            {
                _ when key.EndsWith(".clean", StringComparison.Ordinal) => key[..^".clean".Length],
                _ when key.EndsWith(".smudge", StringComparison.Ordinal) => key[..^".smudge".Length],
                _ when key.EndsWith(".process", StringComparison.Ordinal) => key[..^".process".Length],
                _ => null,
            };
            if (driver is null)
            {
                continue;
            }

            if (driver.Length > MaximumFilterKeyChars)
            {
                return null;
            }

            drivers.Add(driver);
            if (drivers.Count > MaximumFilterDrivers)
            {
                return null;
            }
        }

        var entries = new List<(string Key, string Value)>(drivers.Count * 4);
        foreach (var driver in drivers)
        {
            entries.Add((driver + ".clean", string.Empty));
            entries.Add((driver + ".smudge", string.Empty));
            entries.Add((driver + ".process", string.Empty));
            entries.Add((driver + ".required", "false"));
        }

        if (entries.Count > MaximumFilterConfigEntries)
        {
            return null;
        }

        // The Windows environment block is finite; a malicious config with huge
        // driver names must fail closed instead of producing process_start_failed.
        long totalChars = 0;
        foreach (var entry in entries)
        {
            totalChars += entry.Key.Length + entry.Value.Length + 4;
        }

        if (totalChars > 16_000)
        {
            return null;
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < entries.Count; index++)
        {
            environment[$"GIT_CONFIG_KEY_{index}"] = entries[index].Key;
            environment[$"GIT_CONFIG_VALUE_{index}"] = entries[index].Value;
        }

        environment["GIT_CONFIG_COUNT"] = entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return environment;
    }

    public async Task<GitCliResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            throw new ArgumentException("at least one git argument is required", nameof(arguments));
        }

        // Fixed defenses for the read verbs the Broker itself performs: no optional
        // index locks, never a repo-supplied fsmonitor program, and never a
        // repo-supplied clean/smudge/process filter. Mutating verbs (add, commit,
        // worktree checkout, ...) keep repository filters: those filters are part
        // of what the mutation must do (LFS, repository transforms).
        var filterEnvironment = DisablesRepositoryFilters(arguments)
            ? await FilterEnvironmentAsync(workingDirectory, cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, string>();
        if (filterEnvironment is null)
        {
            // A truncated or unreadable filter table means we cannot prove the
            // read is filter-free; fail closed instead of executing repo config.
            return new GitCliResult(false, -1, string.Empty, string.Empty, "git_filters_unreadable");
        }

        var argv = new List<string>(arguments.Count + 3)
        {
            "--no-optional-locks",
            "-c",
            "core.fsmonitor=false",
        };
        foreach (var argument in arguments)
        {
            argv.Add(argument);
        }

        var environment = _environment;
        if (filterEnvironment.Count > 0)
        {
            var withFilters = new Dictionary<string, string>(_environment, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in filterEnvironment)
            {
                withFilters[pair.Key] = pair.Value;
            }

            environment = withFilters;
        }

        if (additionalEnvironment is { Count: > 0 })
        {
            var merged = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in additionalEnvironment)
            {
                merged[pair.Key] = pair.Value;
            }

            environment = merged;
        }

        var spec = new ProcessRunSpec(
            "git",
            argv,
            workingDirectory,
            environment,
            _timeoutMs,
            _outputLimitBytes,
            HeadCapacityChars: _outputLimitBytes,
            TailCapacityChars: 0);
        var result = await _runner.RunAsync(spec, onDelta: null, cancellationToken);

        if (result.Status == "timed_out")
        {
            return new GitCliResult(false, -1, string.Empty, string.Empty, "git_timed_out");
        }

        if (result.Status == "cancelled")
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (result.Status != "succeeded" && result.Status != "failed")
        {
            return new GitCliResult(false, -1, string.Empty, result.StderrHead, result.StderrHead);
        }

        if (result.ExitCode is null)
        {
            return new GitCliResult(false, -1, string.Empty, result.StderrHead, result.StderrHead);
        }

        if (result.StdoutDroppedBytes > 0 || result.StderrDroppedBytes > 0)
        {
            // Truncated Git output is unknown state; callers must never read it as clean.
            return new GitCliResult(false, -1, string.Empty, string.Empty, "git_output_truncated");
        }

        return new GitCliResult(
            Succeeded: result.ExitCode == 0,
            ExitCode: result.ExitCode ?? -1,
            Stdout: result.StdoutTail.Length == 0
                ? result.StdoutHead
                : result.StdoutHead + result.StdoutTail,
            Stderr: result.StderrTail.Length == 0
                ? result.StderrHead
                : result.StderrHead + result.StderrTail,
            FailureReason: result.ExitCode == 0 ? string.Empty : $"git_exit_{result.ExitCode}");
    }
}
