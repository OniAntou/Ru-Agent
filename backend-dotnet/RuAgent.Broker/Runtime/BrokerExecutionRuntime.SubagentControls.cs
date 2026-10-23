using System.Text.Json;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    public Task<bool> IsToolAdvertisedAsync(string sessionId, string runId, string toolId, CancellationToken ct) =>
        UnderLifecycleAsync(() => _session.Owns(sessionId) && _session.TryGetRunLease(runId, out var lease)
            && lease is not null && lease.SessionId == sessionId && !lease.IsExpired(DateTimeOffset.UtcNow)
            && lease.AdvertisedCapabilities.Contains(toolId), ct);

    /// <summary>
    /// The Host may orchestrate only inside this callback after exact run/call-bound
    /// authorization was consumed. Production still needs advertised capabilities;
    /// this callback does not advertise capabilities or grant a new scope.
    /// </summary>
    public async Task<T> AuthorizeSubagentControlAsync<T>(AgentEnvelope request,
        Func<AuthorizedSubagentControl, CancellationToken, Task<T>> dispatch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        var runId = ReadRunIdOrSession(request);
        using var operation = _runCancellations.Register(runId, ct);
        BrokerRunLease root;
        try
        {
            await _lifecycleGate.WaitAsync(operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && operation.Token.IsCancellationRequested)
        {
            throw new AgentProtocolException("subagent_parent_revoked");
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var toolId = PayloadReads.OptionalString(request.Payload, "tool_id") ?? string.Empty;
            if (!SubagentContractValidator.IsControlTool(toolId)) throw new AgentProtocolException("invalid_subagent_control");
            if (!TryGetOwnedRunLease(request, runId, out var owned, out var error) || owned is null)
                throw new AgentProtocolException(error);
            root = owned;
            var prepared = PrepareToolExecute(request);
            if (prepared.Error is { } refused) throw new AgentProtocolException(refused.ErrorCode ?? "subagent_control_denied");
        }
        finally { _lifecycleGate.Release(); }

        bool IsCurrent() => Volatile.Read(ref _disposed) == 0
            && !_delegations.ContainsKey(runId) && !root.Lifetime.IsRevoked
            && TryGetOwnedRunLease(request, runId, out var current, out _) && current is not null
            && current.Lifetime == root.Lifetime && current.BindingVersion == root.BindingVersion && current.SnapshotHash == root.SnapshotHash
            && current.SnapshotVersion == root.SnapshotVersion && current.CanonicalRoot == root.CanonicalRoot;
        var intent = new AuthorizedSubagentControl(runId, request.SessionId,
            PayloadReads.OptionalString(request.Payload, "tool_id")!, PayloadReads.OptionalString(request.Payload, "tool_call_id")!,
            JsonSerializer.SerializeToElement(request.Payload["arguments"]), IsCurrent, operation.Token);
        try
        {
            intent.DemandActive();
            var result = await dispatch(intent, operation.Token).ConfigureAwait(false);
            intent.DemandActive();
            return result;
        }
        finally { intent.Expire(); }
    }

    private PolicyContext BindSubagentControlContext(AgentEnvelope request, BrokerRunLease lease, PolicyContext context)
    {
        if (!SubagentContractValidator.IsControlTool(context.ToolId)) return context;
        if (_delegations.ContainsKey(lease.RunId)) throw new PolicyContextException("subagent_depth_denied");
        if (lease.Lifetime.IsRevoked) throw new PolicyContextException("subagent_parent_revoked");
        var invocation = PayloadReads.OptionalString(request.Payload, "tool_call_id");
        if (!SubagentContractValidator.IsValidId(invocation)) throw new PolicyContextException("invalid_subagent_invocation");
        if (lease.Preset == "plan" && context.ToolId == "agent_spawn"
            && request.Payload["arguments"]?["role"]?.GetValue<string>() == "coding")
            throw new PolicyContextException("subagent_role_denied");
        // The call ID is part of the authorization, not just the orchestration
        // idempotency key: approval for one call cannot launch an identical second one.
        return context with { IntentHash = FileHashService.ComputeBytesHash(JsonSerializer.SerializeToUtf8Bytes(new
            { context.IntentHash, InvocationId = invocation, LeaseGeneration = lease.Lifetime.Id })) };
    }
}
