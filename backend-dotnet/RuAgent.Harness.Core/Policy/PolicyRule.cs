using RuAgent.Harness.Core;

namespace RuAgent.Harness.Core.Policy;

public sealed record PolicyRule(
    PolicyRuleId RuleId,
    PolicyRuleScope Scope,
    PolicyDecision Decision,
    PolicyMatcher Matcher,
    long CreationOrder,
    DateTimeOffset? ExpiresAt);
