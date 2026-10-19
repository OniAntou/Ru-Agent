using RuAgent.Broker.Policy;
using BrokerRunLease = RuAgent.Broker.Runtime.BrokerRunLease;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Subagents;

internal static class DelegationParentPolicy
{
    public static BoundPolicySnapshot ValidateCeiling(BrokerRunLease parent, BrokerPolicySnapshotWire wire)
    {
        var ceiling = BoundPolicySnapshot.Parse(wire);
        if (!Compatible(parent.Snapshot, ceiling)) throw new AgentProtocolException("subagent_delegation_stale");
        return ceiling;
    }

    // Remembered session approvals append allows. Removing those allows cannot widen
    // the child's original ceiling; changed/removed base rules and new asks/denies revoke it.
    public static bool Compatible(BoundPolicySnapshot current, BoundPolicySnapshot ceiling)
    {
        if (current.Preset != ceiling.Preset || current.Version < ceiling.Version
            || current.WorkspaceId != ceiling.WorkspaceId || current.VolumeSerialNumber != ceiling.VolumeSerialNumber
            || current.RootFileId != ceiling.RootFileId || current.DisplayIdentityHash != ceiling.DisplayIdentityHash
            || current.OrderedRules.Count < ceiling.OrderedRules.Count) return false;
        for (var index = 0; index < ceiling.OrderedRules.Count; index++)
            if (!Same(current.OrderedRules[index], ceiling.OrderedRules[index])) return false;
        return current.OrderedRules.Skip(ceiling.OrderedRules.Count).All(rule => rule.Scope == "run" && rule.Decision == "allow");
    }

    private static bool Same(PolicyRuleSpec left, PolicyRuleSpec right)
    {
        var a = left.Matcher;
        var b = right.Matcher;
        return left.RuleId == right.RuleId && left.Scope == right.Scope && left.Decision == right.Decision
            && left.CreationOrder == right.CreationOrder && left.ExpiresAt == right.ExpiresAt
            && a.ToolId == b.ToolId && a.ToolVersion == b.ToolVersion && a.CanonicalPathPattern == b.CanonicalPathPattern
            && a.ResolvedExecutable == b.ResolvedExecutable && a.ArgvPattern.SequenceEqual(b.ArgvPattern, StringComparer.Ordinal)
            && a.CwdPattern == b.CwdPattern && a.OperationClass == b.OperationClass && a.NetworkIntent == b.NetworkIntent;
    }
}
