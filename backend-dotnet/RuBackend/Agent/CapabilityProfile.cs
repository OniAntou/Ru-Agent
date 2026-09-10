using RuAgent.Broker.Policy;

namespace RuBackend.Agent;

// Wire helpers for the single canonical profile enum owned by the Broker
// (RuAgent.Broker.Policy.CapabilityProfile). RuBackend must not declare its
// own copy: a second enum drifts the moment a tier is added.
public static class CapabilityProfileWireExtensions
{
    public static string ToWireValue(this CapabilityProfile profile) => profile switch
    {
        CapabilityProfile.Agent => "agent",
        _ => "chat",
    };

    public static CapabilityProfile FromWireValue(string value) => value switch
    {
        "agent" => CapabilityProfile.Agent,
        "full" => CapabilityProfile.Agent,
        _ => CapabilityProfile.Chat,
    };
}
