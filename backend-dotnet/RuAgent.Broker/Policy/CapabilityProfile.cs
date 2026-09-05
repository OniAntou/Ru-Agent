namespace RuAgent.Broker.Policy;

// `Full` remains a wire-compatible Broker capability profile. Agent API
// permission presets decide whether it may be selected, but the Broker still
// needs to understand the profile for v1/v2 clients and for lease snapshots.
public enum CapabilityProfile { Chat, Agent, Full }

public static class CapabilityProfileExtensions
{
    public static IReadOnlySet<string> Scopes(this CapabilityProfile profile) =>
        profile switch
        {
            CapabilityProfile.Agent => new HashSet<string>(
                [ToolScope.WorkspaceRead, ToolScope.WorkspaceWrite,
                 ToolScope.TerminalExecute,
                 ToolScope.SkillExecute], StringComparer.Ordinal),
            CapabilityProfile.Full => new HashSet<string>(
                [ToolScope.WorkspaceRead, ToolScope.WorkspaceWrite,
                 ToolScope.TerminalExecute,
                 ToolScope.TerminalNetwork, ToolScope.SkillExecute], StringComparer.Ordinal),
            _ => new HashSet<string>([ToolScope.WorkspaceRead], StringComparer.Ordinal),
        };
}
