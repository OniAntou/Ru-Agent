using RuAgent.Harness.Core;

namespace RuAgent.Harness.Core.Policy;

public sealed record PolicyRule(
    PolicyRuleId RuleId,
    PolicyRuleScope Scope,
    PolicyDecision Decision,
    PolicyMatcher Matcher,
    long CreationOrder,
    DateTimeOffset? ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiry && now >= expiry;

    internal void Validate()
    {
        _ = RuleId.Value;

        if (!Enum.IsDefined(Scope))
        {
            throw new ArgumentOutOfRangeException(nameof(Scope), $"Unknown rule scope '{Scope}'.");
        }

        if (!Enum.IsDefined(Decision))
        {
            throw new ArgumentOutOfRangeException(nameof(Decision), $"Unknown decision '{Decision}'.");
        }

        ArgumentNullException.ThrowIfNull(Matcher);
        Matcher.Validate();

        if (CreationOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CreationOrder), "Creation order must be non-negative.");
        }

        if (ExpiresAt is { } expires)
        {
            if (expires == default)
            {
                throw new ArgumentException("Expiry must be a valid timestamp.", nameof(ExpiresAt));
            }
        }
    }
}
