using RuAgent.Broker.Tools;

namespace RuAgent.Broker.Policy;

public sealed record PolicyGrant(
    string SessionId,
    IReadOnlySet<string> Scopes,
    DateTimeOffset ExpiresAt);

public sealed record ToolPolicyDecision(
    bool Allowed,
    bool RequiresApproval,
    string Code);

public sealed class ToolPolicyEngine
{
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
        if (manifest.Id == "terminal.shell" && grant.Scopes.Contains(ToolScope.TerminalNetwork))
        {
            approval = false;
        }

        return new(true, approval, "allowed");
    }
}
