using RuAgent.Protocol;

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
