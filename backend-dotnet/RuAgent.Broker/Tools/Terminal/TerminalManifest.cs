using RuAgent.Broker.Policy;

namespace RuAgent.Broker.Tools.Terminal;

public sealed record TerminalRunRequest(
    string Shell,
    string RelativeWorkingDirectory,
    string Executable,
    IReadOnlyList<string> Arguments,
    int TimeoutMs = 30_000,
    bool Network = false);

public sealed record TerminalShellRequest(
    string Shell,
    string RelativeWorkingDirectory,
    string Script,
    int TimeoutMs,
    string ApprovalToken,
    string RequestId = "",
    string SessionId = "",
    string ArgumentsHash = "");

public static class TerminalManifests
{
    public static ToolManifest Run { get; } = new(
        "terminal.run",
        "1.0.0",
        "terminal",
        RiskClass.Reversible,
        new HashSet<string>([ToolScope.TerminalExecute], StringComparer.Ordinal),
        new System.Text.Json.Nodes.JsonObject { ["type"] = "object" },
        new System.Text.Json.Nodes.JsonObject { ["type"] = "object" },
        120_000,
        256 * 1024,
        "builtin");

    public static ToolManifest Shell { get; } = Run with
    {
        Id = "terminal.shell",
        Risk = RiskClass.Destructive,
        Scopes = new HashSet<string>([ToolScope.TerminalExecute], StringComparer.Ordinal),
    };
}
