using System.Collections.Frozen;

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

    /// <summary>
    /// Frozen: the set is shared process-wide, and a mutable <see cref="HashSet{T}"/>
    /// behind <see cref="IReadOnlySet{T}"/> is only one cast away from being edited.
    /// </summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [WorkspaceRead, WorkspaceWrite, TerminalExecute, NetworkSearch, NetworkFetch, SystemRead, UserInput],
        StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal);
}
