namespace RuAgent.Broker.Policy;

// Single Agent profile: the wire keeps Chat vs Agent for hello routing, but all
// Agent runs share one policy. TerminalNetwork stays a real, never-granted
// scope so the network check remains fail-closed audit metadata rather than a
// selectable second tier.
public enum CapabilityProfile { Chat, Agent }

public static class CapabilityProfileExtensions
{
    public static IReadOnlySet<string> Scopes(this CapabilityProfile profile) =>
        profile switch
        {
            CapabilityProfile.Agent => new HashSet<string>(
                [ToolScope.WorkspaceRead, ToolScope.WorkspaceWrite,
                 ToolScope.TerminalExecute, ToolScope.WebSearch, ToolScope.WebFetch,
                 ToolScope.SystemRead,
                 ToolScope.SkillExecute], StringComparer.Ordinal),
            _ => new HashSet<string>(
                [ToolScope.WorkspaceRead, ToolScope.SystemRead,
                 ToolScope.WebSearch, ToolScope.WebFetch], StringComparer.Ordinal),
        };
}
