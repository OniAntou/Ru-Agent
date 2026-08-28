namespace RuBackend.Agent;

public enum CapabilityProfile { Chat, Agent }

public static class CapabilityProfileExtensions
{
    public static string ToWireValue(this CapabilityProfile profile) => profile switch
    {
        CapabilityProfile.Agent => "agent",
        _ => "chat",
    };
}
