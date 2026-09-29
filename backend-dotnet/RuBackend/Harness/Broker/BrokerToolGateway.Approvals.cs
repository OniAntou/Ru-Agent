using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Protocol;

namespace RuBackend.Harness.Broker;

public sealed partial class BrokerToolGateway
{
    /// Forwards an approval resolution to the Broker on this gateway's
    /// connection (whose session owns the run lease). On success the returned
    /// one-time authorization token is stored under the original intent key so
    /// the resumed proposal evaluates and executes without a new ask.
    public async Task<(bool Authorized, string? AuthorizationToken)> RespondApprovalAsync(
        string brokerApprovalId,
        string command,
        string argsKey,
        CancellationToken cancellationToken,
        BrokerRunLeaseIdentity? expectedLease = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerApprovalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(argsKey);
        if (expectedLease is not null
            && GetCurrentLease(GetBinding()) != expectedLease)
        {
            return (false, null);
        }

        try
        {
            return await RpcAsync(
                    AgentMessageTypes.ApprovalRespond,
                    binding => new JsonObject
                    {
                        ["run_id"] = binding.RunId,
                        ["approval_id"] = brokerApprovalId,
                        ["command"] = command,
                    },
                    (reply, lease) =>
                    {
                        if (expectedLease is not null && lease != expectedLease)
                        {
                            return (false, (string?)null);
                        }

                        if (reply.MessageType != AgentMessageTypes.ApprovalResult
                            || reply.Payload["status"]?.GetValue<string>() != "authorized")
                        {
                            return (false, (string?)null);
                        }

                        var token = reply.Payload["authorization_token"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(token))
                        {
                            return (false, (string?)null);
                        }

                        if (_approvals is not null
                            && (lease is null
                                || !_approvals.StoreAuthorizationToken(
                                    lease.RunId,
                                    lease,
                                    argsKey,
                                    token,
                                    DateTimeOffset.UtcNow.AddSeconds(60))))
                        {
                            return (false, (string?)null);
                        }

                        return (true, token);
                    },
                    cancellationToken,
                    expectedLease)
                .ConfigureAwait(false);
        }
        catch (ToolTransportException error) when (
            error.Message is "broker_lease_generation_changed" or "gateway_binding_changed")
        {
            return (false, null);
        }
    }

    public async Task<bool> RevokeApprovalAsync(
        ApprovalId approvalId,
        CancellationToken cancellationToken)
    {
        var binding = GetBinding();
        var lease = GetCurrentLease(binding);
        if (lease is null
            || !_approvals!.TryGetAsk(
                binding.RunId,
                lease,
                approvalId.Value,
                out var brokerApprovalId,
                out _))
        {
            return false;
        }

        return await RpcAsync(
                AgentMessageTypes.ApprovalRespond,
                current => new JsonObject
                {
                    ["run_id"] = current.RunId,
                    ["approval_id"] = brokerApprovalId,
                    ["command"] = "deny",
                },
                (reply, responseLease) =>
                {
                    var revoked = responseLease == lease
                        && reply.MessageType == AgentMessageTypes.ApprovalResult
                        && string.Equals(
                            reply.Payload["status"]?.GetValue<string>(),
                            ToolResultStatuses.Denied,
                            StringComparison.Ordinal)
                        && string.Equals(
                            reply.Payload["error_code"]?.GetValue<string>(),
                            "approval_denied",
                            StringComparison.Ordinal);
                    if (revoked)
                    {
                        _approvals.RemoveAsk(binding.RunId, lease, approvalId.Value);
                    }

                    return revoked;
                },
                cancellationToken,
                lease)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes one Agent session's directory grants in the Broker process.
    /// Returns false only when a live Broker explicitly denies; transport
    /// failures propagate so the caller can treat Broker-unavailable as safe.
    /// </summary>
    public async Task<bool> RevokeSessionGrantsAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var reply = await RpcAsync(
            AgentMessageTypes.SessionGrantsRevoke,
            new JsonObject { ["agent_session_id"] = sessionId },
            cancellationToken).ConfigureAwait(false);
        return reply.MessageType == AgentMessageTypes.SessionGrantsRevoked
            && string.Equals(
                reply.Payload["status"]?.GetValue<string>(),
                ToolResultStatuses.Succeeded,
                StringComparison.Ordinal);
    }

    internal static string ApprovalArgsKey(string toolId, JsonElement arguments)
    {
        var canonicalArguments = CanonicalizeJson(JsonNode.Parse(arguments.GetRawText()));
        return toolId + "|" + (canonicalArguments?.ToJsonString() ?? "null");
    }

    private static JsonNode? CanonicalizeJson(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject(
            obj.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => KeyValuePair.Create<string, JsonNode?>(item.Key, CanonicalizeJson(item.Value)))),
        JsonArray array => new JsonArray(array.Select(item => (JsonNode?)CanonicalizeJson(item)).ToArray()),
        _ => node.DeepClone(),
    };

    private static ApprovalId InternalApprovalId(string brokerApprovalId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(brokerApprovalId));
        return new ApprovalId("apr_" + Convert.ToHexString(digest, 0, 16).ToLowerInvariant());
    }
}
