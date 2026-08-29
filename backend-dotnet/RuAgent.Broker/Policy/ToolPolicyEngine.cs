using RuAgent.Broker.Tools;

namespace RuAgent.Broker.Policy;

public sealed record PolicyGrant(
    string SessionId,
    IReadOnlySet<string> Scopes,
    DateTimeOffset ExpiresAt,
    IReadOnlySet<string>? SkillIds = null);

public sealed record ToolPolicyDecision(
    bool Allowed,
    bool RequiresApproval,
    string Code);

public sealed class ToolPolicyEngine
{
    public ToolPolicyDecision EvaluateSkill(
        PolicyGrant grant,
        string skillId,
        IReadOnlySet<string> requiredScopes)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentNullException.ThrowIfNull(requiredScopes);

        if (grant.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return new(false, false, "session_expired");
        }

        if (!grant.Scopes.Contains(ToolScope.SkillExecute))
        {
            return new(false, false, "scope_denied");
        }

        if (grant.SkillIds is null || !grant.SkillIds.Contains(skillId))
        {
            return new(false, false, "skill_not_granted");
        }

        if (!requiredScopes.All(grant.Scopes.Contains))
        {
            return new(false, false, "scope_denied");
        }

        return new(true, false, "allowed");
    }

    public ToolPolicyDecision Evaluate(PolicyGrant grant, ToolManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(manifest);
        if (grant.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return new(false, false, "session_expired");
        }

        if (!manifest.Scopes.All(grant.Scopes.Contains))
        {
            return new(false, false, "scope_missing");
        }

        var approval = manifest.Risk is RiskClass.External or RiskClass.Destructive
            || manifest.RequiresApproval;

        return new(true, approval, "allowed");
    }
}
