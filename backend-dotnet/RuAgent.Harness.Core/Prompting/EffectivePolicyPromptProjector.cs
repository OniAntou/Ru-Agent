using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core.Prompting;

public static class EffectivePolicyPromptProjector
{
    private static readonly AgentPolicySubject[] OrderedSubjects =
    [
        AgentPolicySubject.WorkspaceRead,
        AgentPolicySubject.Web,
        AgentPolicySubject.WorkspaceMutation,
        AgentPolicySubject.BoundedLocalTerminal,
        AgentPolicySubject.ExternalStructuredFile,
        AgentPolicySubject.RiskyTerminal,
        AgentPolicySubject.StructuredProtectedPath,
        AgentPolicySubject.HardInvalid,
    ];

    public static string Project(AgentPolicyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var lines = new List<string>
        {
            "effective_policy:",
            $"profile_version: {profile.Version}",
            $"preset: {profile.Preset}",
            $"terminal_authority: {AgentPolicyProfileCatalog.DescribeTerminalAuthority(profile.TerminalAuthority)}",
        };
        lines.AddRange(OrderedSubjects.Select(subject =>
            $"{AgentPolicyProfileCatalog.Describe(subject)}={Describe(profile.DecisionFor(subject))}"));
        lines.Add("broker_authority: true");
        return string.Join("\n", lines);
    }

    private static string Describe(AgentPolicyDecision decision) => decision switch
    {
        AgentPolicyDecision.Allow => BrokerPolicyContracts.AllowWireValue,
        AgentPolicyDecision.Ask => BrokerPolicyContracts.AskWireValue,
        AgentPolicyDecision.Deny => BrokerPolicyContracts.DenyWireValue,
        _ => throw new AgentProtocolException("invalid_decision"),
    };
}
