using RuAgent.Broker.Tools.Terminal;

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
    private const int OutputLimitBytes = 256 * 1024;

    private readonly StreamingProcessRunner _runner;
    private readonly IReadOnlyDictionary<string, string> _environment;

    public StreamingGitCli(
        StreamingProcessRunner? runner = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        _runner = runner ?? new StreamingProcessRunner();
        _environment = environment ?? new AgentCommandEnvironment().ChildEnvironment;
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

        // Fixed defenses for every read the Broker itself performs: no optional
        // index locks, and never a repo-supplied fsmonitor program. The same rule
        // covers the model-visible terminal path, which pins these flags in
        // StreamingProcessRunner.
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
        if (additionalEnvironment is { Count: > 0 })
        {
            var merged = new Dictionary<string, string>(_environment, StringComparer.OrdinalIgnoreCase);
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
            TimeoutMs,
            OutputLimitBytes);
        var result = await _runner.RunAsync(spec, onDelta: null, cancellationToken);

        if (result.Status == "timed_out")
        {
            return new GitCliResult(false, -1, string.Empty, string.Empty, "git_timed_out");
        }

        if (result.Status == "cancelled")
        {
            return new GitCliResult(false, -1, string.Empty, string.Empty, "git_cancelled");
        }

        if (result.Status != "succeeded" && result.Status != "failed")
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
