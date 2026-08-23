namespace RuAgent.Broker.Policy;

public enum CapabilityProfile { Chat, Agent, Full }

public static class CapabilityProfileExtensions
{
    public static IReadOnlySet<string> Scopes(this CapabilityProfile profile) =>
        profile switch
        {
            CapabilityProfile.Agent => new HashSet<string>(
                [ToolScope.WorkspaceRead, ToolScope.WorkspaceWrite,
                 ToolScope.TerminalExecute, ToolScope.SystemWrite], StringComparer.Ordinal),
            CapabilityProfile.Full => new HashSet<string>(
                [ToolScope.WorkspaceRead, ToolScope.WorkspaceWrite,
                 ToolScope.TerminalExecute, ToolScope.SystemWrite,
                 ToolScope.TerminalNetwork], StringComparer.Ordinal),
            _ => new HashSet<string>([ToolScope.WorkspaceRead], StringComparer.Ordinal),
        };
}
