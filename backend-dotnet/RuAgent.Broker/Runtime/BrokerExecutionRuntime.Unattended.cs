using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Protocol;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    /// <summary>
    /// Whether a call to <paramref name="toolId"/> with these arguments would be allowed
    /// without an approval under the run's bound policy. Answers from the same evaluator
    /// as a real proposal but issues no token and records no pending approval.
    /// </summary>
    /// <remarks>
    /// Used to decide whether a run may search through its model provider (ADR 0033): a
    /// provider-hosted search cannot stop for an approval, so it is used only where the
    /// Broker's own <c>web_search</c> would run unattended. Anything but a plain allow,
    /// including a closed lease or a delegated child run, answers false.
    /// </remarks>
    public bool AllowsWithoutApproval(string sessionId, string runId, string toolId, string toolVersion, JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var request = new AgentEnvelope(AgentEnvelopeCodec.ProtocolVersionV2, "unattended-check",
            AgentMessageTypes.ToolEvaluate, sessionId, null, 10_000, new JsonObject());
        if (!TryGetOwnedRunLease(request, runId, out var lease, out _) || lease is null
            || lease.Lifetime.IsRevoked || _delegations.ContainsKey(runId)
            || !lease.AdvertisedCapabilities.Contains(toolId))
            return false;
        PolicyContext context;
        try
        {
            context = new PolicyContextFactory(lease).Create(toolId, toolVersion, arguments);
        }
        catch (PolicyContextException)
        {
            return false;
        }

        var evaluation = new PolicySnapshotEvaluator(_session.DirectoryGrants)
            .Evaluate(lease.Snapshot, context, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);
        return string.Equals(evaluation.Decision, "allow", StringComparison.Ordinal);
    }
}
