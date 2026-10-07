namespace RuAgent.Protocol;

public static class AgentToolScopes
{
    public const string WorkspaceRead = "workspace.read";
    public const string WorkspaceWrite = "workspace.write";
    public const string TerminalExecute = "terminal.execute";
    public const string NetworkSearch = "network.search";
    public const string NetworkFetch = "network.fetch";
    public const string SystemRead = "system.read";
    public const string UserInput = "user.input";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [WorkspaceRead, WorkspaceWrite, TerminalExecute, NetworkSearch, NetworkFetch, SystemRead, UserInput],
        StringComparer.Ordinal);
}
