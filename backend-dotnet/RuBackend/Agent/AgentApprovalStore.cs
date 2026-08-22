using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RuBackend.Agent;

public sealed class AgentApprovalStore
{
    private readonly ConcurrentDictionary<string, AgentApprovalRequest> _requests = new(StringComparer.Ordinal);

    public static string ComputeArgumentsHash(
        string toolId,
        string toolVersion,
        JsonObject arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        ArgumentNullException.ThrowIfNull(arguments);
        var canonical = new JsonObject
        {
            ["tool_id"] = toolId,
            ["tool_version"] = toolVersion,
            ["arguments"] = Canonicalize(arguments),
        };
        var bytes = Encoding.UTF8.GetBytes(canonical.ToJsonString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public void Put(AgentApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId)
            || string.IsNullOrWhiteSpace(request.SessionId)
            || string.IsNullOrWhiteSpace(request.ArgumentsHash))
        {
            throw new ArgumentException("Approval identity is incomplete.", nameof(request));
        }

        _requests[request.RequestId] = request;
    }

    public bool TryConsume(
        string requestId,
        string sessionId,
        string toolId,
        string toolVersion,
        JsonObject arguments,
        out AgentApprovalRequest? request,
        DateTimeOffset? now = null)
    {
        request = null;
        if (!_requests.TryGetValue(requestId, out var candidate))
        {
            return false;
        }

        if (candidate.ExpiresAt <= (now ?? DateTimeOffset.UtcNow)
            || !string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(candidate.ToolId, toolId, StringComparison.Ordinal)
            || !string.Equals(candidate.ToolVersion, toolVersion, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(candidate.ArgumentsHash),
                Encoding.UTF8.GetBytes(ComputeArgumentsHash(toolId, toolVersion, arguments))))
        {
            return false;
        }

        if (!_requests.TryRemove(new KeyValuePair<string, AgentApprovalRequest>(requestId, candidate)))
        {
            return false;
        }

        request = candidate;
        return true;
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
