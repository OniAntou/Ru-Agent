using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Policy;

/// <summary>
/// What a granted approval may remember. <see cref="SessionMatcher"/> is the exact intent for the agent
/// session; <see cref="DurableMatcher"/> is the broader pattern an allow-always saves to the project.
/// A null matcher means that scope may not remember the approval.
/// </summary>
public sealed record ApprovalRememberPlan(
    BrokerPolicyMatcherWire? SessionMatcher,
    BrokerPolicyMatcherWire? DurableMatcher)
{
    public static ApprovalRememberPlan None { get; } = new(null, null);

    public bool IsRememberable => SessionMatcher is not null || DurableMatcher is not null;
}
