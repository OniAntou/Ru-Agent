using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Subagents;

public static class DelegationPolicyEvaluator
{
    public static PolicyEvaluation Evaluate(DelegatedExecutionScope scope, BrokerRunLease parent,
        PolicyContext physicalContext, DateTimeOffset now)
    {
        if (!string.Equals(physicalContext.CanonicalWorkspaceRoot, scope.ChildRoot, StringComparison.OrdinalIgnoreCase))
            return Deny("subagent_scope_denied");
        if (physicalContext.ToolId.StartsWith("agent_", StringComparison.Ordinal)) return Deny("subagent_depth_denied");
        if (!SubagentRoleTools.Includes(scope.Role, physicalContext.ToolId))
            return Deny("subagent_role_denied");
        if (physicalContext.Target is { } target && !Inside(scope.ChildRoot, target.CanonicalPath))
            return Deny("subagent_scope_denied");
        if (physicalContext.CanonicalCwd is { } cwd && !Inside(scope.ChildRoot, cwd))
            return Deny("subagent_scope_denied");
        try
        {
            var resolver = new CanonicalPathResolver(scope.ParentRoot);
            ResolvedPath? logicalTarget = physicalContext.Target is { } physicalTarget
                ? resolver.Resolve(Path.GetRelativePath(scope.ChildRoot, physicalTarget.CanonicalPath)) : null;
            var logicalCwd = physicalContext.CanonicalCwd is { } physicalCwd
                ? resolver.Resolve(Path.GetRelativePath(scope.ChildRoot, physicalCwd)) : null;
            if (logicalTarget is { } resolved && !Inside(scope.ParentRoot, resolved.CanonicalPath)
                || logicalCwd is { } directory && !Inside(scope.ParentRoot, directory.CanonicalPath))
                return Deny("subagent_scope_denied");
            var logical = physicalContext with { CanonicalWorkspaceRoot = scope.ParentRoot,
                Target = logicalTarget, CanonicalCwd = logicalCwd?.CanonicalPath, WorkingDirectory = logicalCwd,
                SessionId = scope.ChildSessionId };
            // Fresh registry: a child's authority must not inherit parent directory grants.
            var evaluator = new PolicySnapshotEvaluator();
            var relative = evaluator.Evaluate(parent.Snapshot, logical, PolicyRequestInvariants.Valid, now);
            // Preserve absolute rules as well as workspace-relative rules after relocation.
            // Never let an allow from either representation override the other view's ask/deny.
            var absolute = evaluator.Evaluate(parent.Snapshot, logical with { RelativePathInsideWorkspace = null },
                PolicyRequestInvariants.Valid, now);
            return absolute.Decision == "deny" || absolute.Decision == "ask" && relative.Decision == "allow"
                ? absolute : relative;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return Deny("subagent_scope_invalid"); }
    }

    internal static bool Inside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static PolicyEvaluation Deny(string code) => new("deny", code, null);
}
