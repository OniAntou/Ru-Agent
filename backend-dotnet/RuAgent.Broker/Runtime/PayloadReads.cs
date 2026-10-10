using System.Text.Json.Nodes;

namespace RuAgent.Broker.Runtime;

/// <summary>
/// Bounded payload scalar reads shared by the execution runtime and the wire
/// adapter. Non-string, empty and whitespace values read as absent.
/// </summary>
internal static class PayloadReads
{
    public static string? OptionalString(JsonObject payload, string key) =>
        payload[key] is JsonValue value
        && value.TryGetValue<string>(out var result)
        && !string.IsNullOrWhiteSpace(result)
            ? result
            : null;
}
