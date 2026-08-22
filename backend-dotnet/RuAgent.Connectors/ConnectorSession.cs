using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Connectors;

public sealed record ConnectorSession(
    string ConnectorId,
    string SessionId,
    IReadOnlySet<string> Scopes,
    DateTimeOffset ExpiresAt)
{
    public bool Allows(string scope, DateTimeOffset? now = null) =>
        ExpiresAt > (now ?? DateTimeOffset.UtcNow) && Scopes.Contains(scope);
}

public sealed record ConnectorApproval(
    string RequestId,
    string SessionId,
    string ArgumentsHash,
    bool Approved);

public sealed record ConnectorResult(
    string Status,
    string SafeSummary,
    JsonObject Output,
    string? ErrorCode = null);

public static class ConnectorArguments
{
    public static string Hash(string operation, JsonObject arguments)
    {
        var canonical = new JsonObject
        {
            ["operation"] = operation,
            ["arguments"] = Canonicalize(arguments),
        };
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToJsonString())))
            .ToLowerInvariant();
    }

    private static JsonNode? Canonicalize(JsonNode? node)
    {
        if (node is JsonObject objectNode)
        {
            var result = new JsonObject();
            foreach (var pair in objectNode.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                result[pair.Key] = Canonicalize(pair.Value);
            }

            return result;
        }

        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
            {
                result.Add(Canonicalize(item));
            }

            return result;
        }

        return node?.DeepClone();
    }
}
