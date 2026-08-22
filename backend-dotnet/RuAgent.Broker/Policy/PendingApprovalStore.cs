using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;

namespace RuAgent.Broker.Policy;

public sealed record PendingApproval(
    string ApprovalId,
    AgentEnvelope Request,
    ToolManifest Manifest,
    JsonObject Arguments,
    string ArgumentsHash,
    string SafeAction,
    DateTimeOffset ExpiresAt,
    string? TerminalApprovalToken,
    bool Approved);

public sealed class PendingApprovalStore
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);

    public string Issue(
        AgentEnvelope request,
        ToolManifest manifest,
        JsonObject arguments,
        string safeAction,
        DateTimeOffset expiresAt,
        string? terminalApprovalToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeAction);
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt));
        }

        var approvalId = $"approval-{Guid.NewGuid():N}";
        var requestCopy = request with { Payload = request.Payload.DeepClone().AsObject() };
        var argumentsCopy = arguments.DeepClone().AsObject();
        var pending = new PendingApproval(
            approvalId,
            requestCopy,
            manifest,
            argumentsCopy,
            ComputeArgumentsHash(manifest.Id, manifest.Version, argumentsCopy),
            safeAction.Length <= 256 ? safeAction : safeAction[..256],
            expiresAt,
            terminalApprovalToken,
            false);
        _pending[approvalId] = pending;
        return approvalId;
    }

    public bool TryConsume(
        string approvalId,
        string sessionId,
        bool approved,
        DateTimeOffset now,
        out PendingApproval? pending)
    {
        pending = null;
        if (string.IsNullOrWhiteSpace(approvalId)
            || string.IsNullOrWhiteSpace(sessionId)
            || !_pending.TryGetValue(approvalId, out var candidate)
            || candidate.ExpiresAt <= now
            || !string.Equals(candidate.Request.SessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(
                candidate.ArgumentsHash,
                ComputeArgumentsHash(candidate.Manifest.Id, candidate.Manifest.Version, candidate.Arguments),
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!_pending.TryRemove(new KeyValuePair<string, PendingApproval>(approvalId, candidate)))
        {
            return false;
        }

        pending = candidate with { Approved = approved };
        return true;
    }

    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        foreach (var pair in _pending)
        {
            if (string.Equals(pair.Value.Request.SessionId, sessionId, StringComparison.Ordinal))
            {
                _pending.TryRemove(pair.Key, out _);
            }
        }
    }

    public void Clear() => _pending.Clear();

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
