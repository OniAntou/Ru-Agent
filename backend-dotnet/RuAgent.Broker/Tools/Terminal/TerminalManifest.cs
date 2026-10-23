using RuAgent.Broker.Policy;
using RuAgent.Protocol;

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

public static class TerminalManifests
{
    public static ToolManifest Bash { get; } = new(
        "bash",
        "1.0.0",
        "terminal",
        RiskClass.Reversible,
        new HashSet<string>([ToolScope.TerminalExecute], StringComparer.Ordinal),
        AgentModelToolContractCatalog.Get("bash", "1.0.0").InputSchema,
        new System.Text.Json.Nodes.JsonObject { ["type"] = "object" },
        120_000,
        256 * 1024,
        "builtin",
        RequiresApproval: true);
}
