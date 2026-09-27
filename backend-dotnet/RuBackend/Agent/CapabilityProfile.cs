using RuAgent.Broker.Policy;

namespace RuBackend.Agent;

// Wire helper for the single canonical profile enum owned by the Broker
// (RuAgent.Broker.Policy.CapabilityProfile). RuBackend must not declare its
// own copy: a second enum drifts the moment a tier is added.
public static class CapabilityProfileWireExtensions
{
    public static string ToWireValue(this CapabilityProfile profile) => profile switch
    {
        CapabilityProfile.Agent => "agent",
        CapabilityProfile.Chat => "chat",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };
}
