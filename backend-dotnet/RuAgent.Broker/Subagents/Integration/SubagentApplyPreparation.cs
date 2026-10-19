using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Subagents.Integration;

/// <summary>One bounded immutable copy shared by authorization, journal and mutation.</summary>
internal sealed record SubagentApplyPreparation(SubagentChangeSet Changes, string Fingerprint)
{
    public static SubagentApplyPreparation Create(BrokerRunLease root, SubagentChangeSet changes)
    {
        if (root.RunId != changes.ParentRunId || root.IsExpired(DateTimeOffset.UtcNow) || root.Preset == "plan"
            || !string.Equals(root.CanonicalRoot, changes.ParentRoot, StringComparison.OrdinalIgnoreCase)
            || !SubagentContractValidator.IsValidId(changes.AgentId)
            || !SubagentContractValidator.IsValidId(changes.SnapshotId)
            || changes.ResultVersion is < 1 or > SubagentLimits.MaxAttempts)
            throw new AgentProtocolException("subagent_apply_denied");
        if (changes.Files is null || changes.Files.Count > SubagentLimits.MaxSnapshotFiles)
            throw new AgentProtocolException("subagent_changeset_invalid");

        // Check lengths before allocating copies; verify hashes on the copies afterwards.
        long bytes = 0;
        foreach (var file in changes.Files)
        {
            if (file is null || file.Content.Length > FileMutationGuard.MaxRollbackBytes)
                throw new AgentProtocolException("subagent_changeset_invalid");
            bytes += file.Content.Length;
            if (bytes > SubagentLimits.MaxSnapshotBytes) throw new AgentProtocolException("subagent_changeset_too_large");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<SubagentFileChange>(changes.Files.Count);
        foreach (var file in changes.Files)
        {
            var copy = file with { Content = file.Content.ToArray() };
            var context = Resolve(root, copy);
            if (!paths.Add(context.Target!.CanonicalPath) || copy.Mode is not (100644 or 100755))
                throw new AgentProtocolException("subagent_changeset_invalid");
            FileHashService.ValidateExpectedSha256(copy.BaseHash);
            FileHashService.ValidateExpectedSha256(copy.ProposedHash);
            if (copy.ProposedHash == FileHashService.Missing ? copy.Content.Length != 0
                : FileHashService.ComputeBytesHash(copy.Content.Span) != copy.ProposedHash)
                throw new AgentProtocolException("subagent_changeset_invalid");
            files.Add(copy);
        }
        changes = changes with { Files = files.AsReadOnly() };
        var fingerprint = FileHashService.ComputeBytesHash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            changes.AgentId, changes.ParentRunId, changes.ParentRoot, changes.SnapshotId, changes.ResultVersion,
            Files = files.Select(f => new { f.RelativePath, f.BaseHash, f.ProposedHash, f.Mode }),
        }));
        return new(changes, fingerprint);
    }

    public static PolicyContext Resolve(BrokerRunLease root, SubagentFileChange file)
    {
        if (string.IsNullOrWhiteSpace(file.RelativePath) || file.RelativePath.Length > 4096
            || Path.IsPathRooted(file.RelativePath) || file.RelativePath.Contains(':')
            || file.RelativePath.Split(['/', '\\']).Any(p => p is "" or "." or ".."
                || p.Equals(".git", StringComparison.OrdinalIgnoreCase) || p.EndsWith('.') || p.EndsWith(' ')))
            throw new AgentProtocolException("subagent_changeset_invalid");
        var context = new PolicyContextFactory(root).Create("write", "1.0.0", new JsonObject
            { ["path"] = file.RelativePath, ["op"] = file.ProposedHash == FileHashService.Missing ? "delete" : "write" });
        if (context.Target is not { } target || !DelegationPolicyEvaluator.Inside(root.CanonicalRoot, target.CanonicalPath))
            throw new AgentProtocolException("subagent_apply_denied");
        return context;
    }

    public static PolicyEvaluation Evaluate(BrokerRunLease root, PolicyContext context)
    {
        var evaluator = new PolicySnapshotEvaluator();
        var relative = evaluator.Evaluate(root.Snapshot, context, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);
        var canonical = evaluator.Evaluate(root.Snapshot, context with { RelativePathInsideWorkspace = null },
            PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow);
        if (relative.Decision == "deny") return relative;
        if (canonical.Decision == "deny") return canonical;
        return relative.Decision == "ask" ? relative : canonical;
    }
}
