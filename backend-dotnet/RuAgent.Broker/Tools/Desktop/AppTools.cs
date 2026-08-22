using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Desktop;

public sealed class AppTools
{
    private static readonly IReadOnlySet<string> AllowedTargets = new HashSet<string>(
        ["notepad", "calculator", "mspaint"],
        StringComparer.OrdinalIgnoreCase);

    public JsonObject List() => new()
    {
        ["apps"] = new JsonArray(AllowedTargets.Order(StringComparer.OrdinalIgnoreCase).Select(value => (JsonNode?)value).ToArray()),
    };

    public JsonObject Open(string target)
    {
        if (!AllowedTargets.Contains(target))
        {
            throw new UnauthorizedAccessException("app_not_allowlisted");
        }

        return new JsonObject { ["target"] = target, ["status"] = "accepted" };
    }
}
