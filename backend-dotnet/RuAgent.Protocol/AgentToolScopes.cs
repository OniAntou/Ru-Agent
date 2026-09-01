namespace RuAgent.Protocol;

public static class AgentToolScopes
{
    public const string WorkspaceRead = "workspace.read";
    public const string WorkspaceWrite = "workspace.write";
    public const string TerminalExecute = "terminal.execute";
    public const string NetworkSearch = "network.search";
    public const string NetworkFetch = "network.fetch";
    public const string SystemRead = "system.read";
    public const string SystemWrite = "system.write";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [WorkspaceRead, WorkspaceWrite, TerminalExecute, NetworkSearch, NetworkFetch, SystemRead, SystemWrite],
        StringComparer.Ordinal);
}
