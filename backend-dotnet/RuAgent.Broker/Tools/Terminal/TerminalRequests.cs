using RuAgent.Broker.Policy;

namespace RuAgent.Broker.Tools.Terminal;

public sealed record TerminalRunRequest(
    string Shell,
    string RelativeWorkingDirectory,
    string Executable,
    IReadOnlyList<string> Arguments,
    int TimeoutMs = 30_000,
    bool Network = false,
    string ApprovalToken = "",
    string RequestId = "",
    string SessionId = "",
    bool ReadOnly = false,
    IReadOnlyList<string>? WriteSet = null,
    bool Approved = false)
{
    internal CanonicalCommandIntent? AuthorizedIntent { get; init; }
}

public sealed record TerminalShellRequest(
    string Shell,
    string RelativeWorkingDirectory,
    string Script,
    int TimeoutMs,
    string ApprovalToken,
    string RequestId = "",
    string SessionId = "",
    bool ReadOnly = false,
    IReadOnlyList<string>? WriteSet = null,
    bool Approved = false,
    bool Network = false)
{
    internal CanonicalCommandIntent? AuthorizedIntent { get; init; }
}
