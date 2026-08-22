using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Desktop;

public sealed class SystemTools
{
    public JsonObject Time() => new()
    {
        ["utc"] = DateTimeOffset.UtcNow.ToString("O"),
    };

    public JsonObject Status() => new()
    {
        ["os"] = Environment.OSVersion.Platform.ToString(),
        ["processor_count"] = Environment.ProcessorCount,
    };
}
