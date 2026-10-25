using System.Text.Json.Nodes;
using RuAgent.Protocol;

namespace RuBackend.Providers;

internal static class AgentToolProfilePin
{
    internal static string SelectNew()
    {
        try { return AgentToolProfiles.ForNewRun(Environment.GetEnvironmentVariable("RU_AGENT_TOOL_PROFILE")); }
        catch (AgentProtocolException error) { throw new ProviderCatalogException(error.Code); }
    }

    internal static void ValidateAndRemove(JsonObject options)
    {
        try { _ = AgentToolProfiles.ReadPin(options); }
        catch (AgentProtocolException error) { throw new ProviderCatalogException(error.Code); }
        options.Remove(AgentToolProfiles.PinField);
    }
}
