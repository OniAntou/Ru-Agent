namespace RuBackend.Agent;

public enum CapabilityProfile { Chat, Agent, Full }

public static class CapabilityProfileExtensions
{
    public static string ToWireValue(this CapabilityProfile profile) => profile switch
    {
        CapabilityProfile.Agent => "agent",
        CapabilityProfile.Full => "full",
        _ => "chat",
    };
}
