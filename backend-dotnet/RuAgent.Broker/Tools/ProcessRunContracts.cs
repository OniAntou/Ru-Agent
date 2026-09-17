namespace RuAgent.Broker.Tools;

/// <summary>
/// One bounded child-process request. Owned by the tools layer because both the
/// streaming terminal runner and the managed Git CLI build it.
/// </summary>
public sealed record ProcessRunSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int TimeoutMs,
    int OutputLimitBytes);

/// <summary>
/// The receipt of one child process. <c>Status</c> is a bounded stable code
/// (<c>succeeded</c>, <c>failed</c>, <c>timed_out</c>, <c>cancelled</c>); a non-zero
/// exit is never promoted to success and a start failure never reports an exit code.
/// </summary>
public sealed record ProcessRunResult(
    string Status,
    int? ExitCode,
    int DurationMs,
    string Stdout,
    string Stderr,
    bool OutputTruncated,
    string WorkingDirectory);
