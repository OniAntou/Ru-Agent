using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Protocol;

namespace RuBackend.Harness.Broker;

public sealed partial class InProcessToolGateway
{
    /// <summary>
    /// Resolves a pending approval directly on the runtime. On success the
    /// returned one-time authorization token is stored under the original
    /// intent key so the resumed proposal evaluates and executes without a new
    /// ask, exactly like the wire gateway.
    /// </summary>
    public async Task<(bool Authorized, string? AuthorizationToken)> RespondApprovalAsync(
        string brokerApprovalId,
        string command,
        string argsKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerApprovalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(argsKey);

        BrokerRunLeaseIdentity lease;
        lock (_sync)
        {
            if (IsDisposed)
            {
                return (false, null);
            }

            lease = _lease;
        }

        var result = await _runtime
            .RespondApprovalAsync(
                Request(lease.SessionId, AgentMessageTypes.ApprovalRespond, new JsonObject
                {
                    ["run_id"] = _runId,
                    ["approval_id"] = brokerApprovalId,
                    ["command"] = command,
                }),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Refusal is not null
            || !string.Equals(result.Status, "authorized", StringComparison.Ordinal))
        {
            return (false, null);
        }

        var token = result.AuthorizationToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, null);
        }

        if (_approvals is not null
            && !_approvals.StoreAuthorizationToken(
                _runId,
                lease,
                argsKey,
                token,
                DateTimeOffset.UtcNow.AddSeconds(60)))
        {
            return (false, null);
        }

        return (true, token);
    }

    /// <summary>
    /// Denies a pending approval on the runtime and drops its Host-side
    /// mapping when the authority confirms the denial. Unknown or already
    /// consumed approvals answer false, never a throw.
    /// </summary>
    public async Task<bool> RevokeApprovalAsync(
        ApprovalId approvalId,
        CancellationToken cancellationToken)
    {
        BrokerRunLeaseIdentity lease;
        lock (_sync)
        {
            if (IsDisposed)
            {
                return false;
            }

            lease = _lease;
        }

        if (_approvals is null
            || !_approvals.TryGetAsk(_runId, lease, approvalId.Value, out var brokerApprovalId, out _))
        {
            return false;
        }

        var result = await _runtime
            .RespondApprovalAsync(
                Request(lease.SessionId, AgentMessageTypes.ApprovalRespond, new JsonObject
                {
                    ["run_id"] = _runId,
                    ["approval_id"] = brokerApprovalId,
                    ["command"] = "deny",
                }),
                cancellationToken)
            .ConfigureAwait(false);

        var revoked = result.Refusal is null
            && string.Equals(result.Status, ToolResultStatuses.Denied, StringComparison.Ordinal)
            && string.Equals(result.ErrorCode, "approval_denied", StringComparison.Ordinal);
        if (revoked)
        {
            _approvals.RemoveAsk(_runId, lease, approvalId.Value);
        }

        return revoked;
    }

    /// <summary>
    /// Revokes one Agent session's directory grants on the runtime. True when
    /// revoked; false only when the authority explicitly refuses, which must
    /// fail session deletion closed.
    /// </summary>
    public async Task<bool> RevokeSessionGrantsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string ownerSessionId;
        lock (_sync)
        {
            ownerSessionId = _lease.SessionId;
        }

        var result = await _runtime
            .RevokeSessionGrantsAsync(ownerSessionId, sessionId, cancellationToken)
            .ConfigureAwait(false);
        return result.Refusal is null && result.Revoked;
    }

    /// <summary>
    /// Lists the run session's bounded background tasks and optionally stops
    /// one, in the exact payload shape the wire reply carries.
    /// </summary>
    public async Task<JsonObject> ListSessionTasksAsync(
        string? killExecutionId,
        CancellationToken cancellationToken)
    {
        string ownerSessionId;
        lock (_sync)
        {
            ownerSessionId = _lease.SessionId;
        }

        var result = await _runtime
            .SessionTasksAsync(ownerSessionId, killExecutionId, cancellationToken)
            .ConfigureAwait(false);

        if (result.Refusal is not null)
        {
            return new JsonObject
            {
                ["status"] = ToolResultStatuses.Denied,
                ["error_code"] = result.Refusal.ErrorCode,
                ["safe_summary"] = result.Refusal.SafeSummary,
            };
        }

        if (result.Payload is { } payload)
        {
            var mapped = payload.DeepClone().AsObject();
            mapped["status"] = result.Status;
            mapped["safe_summary"] = result.SafeSummary;
            return mapped;
        }

        return new JsonObject
        {
            ["status"] = result.Status,
            ["error_code"] = result.ErrorCode,
            ["safe_summary"] = result.SafeSummary,
        };
    }
}
