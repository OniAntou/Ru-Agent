using RuAgent.Broker.Policy;
using RuAgent.Protocol.Tools;

namespace RuAgent.Broker.Tools;

/// <summary>
/// Maps the Protocol descriptor vocabulary onto the Broker's own identities. Every named member is
/// listed explicitly and the only other arm throws: an unknown value never receives a silent
/// fallback. The compiler always requires a catch-all for enum switches, so a new member is caught at
/// runtime by the mapping coverage test rather than at compile time.
/// </summary>
internal static class ToolDescriptorMapping
{
    public static RiskClass ToRiskClass(AgentToolRisk risk) => risk switch
    {
        AgentToolRisk.Read => RiskClass.Read,
        AgentToolRisk.Reversible => RiskClass.Reversible,
        AgentToolRisk.External => RiskClass.External,
        AgentToolRisk.Destructive => RiskClass.Destructive,
        _ => throw new InvalidOperationException("unmapped_tool_risk"),
    };

    /// <summary>
    /// The adapter string a manifest carries. These literals are part of the trust boundary: the
    /// registry's allowlist is derived from them, so they must not change without a policy review.
    /// </summary>
    public static string ToAdapterName(AgentToolAdapter adapter) => adapter switch
    {
        AgentToolAdapter.Builtin => "builtin",
        AgentToolAdapter.Desktop => "desktop",
        AgentToolAdapter.Terminal => "terminal",
        AgentToolAdapter.Web => "web",
        AgentToolAdapter.Connector => "connector",
        AgentToolAdapter.LanguageServer => "language_server",
        AgentToolAdapter.HostIntercepted => throw new InvalidOperationException("host_intercepted_tool_has_no_adapter"),
        _ => throw new InvalidOperationException("unmapped_tool_adapter"),
    };
}
