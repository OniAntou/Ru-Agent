using System.Text.Json;
using RuAgent.Broker.Subagents;
using RuAgent.Broker.Subagents.Integration;
using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    /// <summary>
    /// Collects an owned private artifact after its session drains, including after
    /// restart or parent completion. It opens no run and grants no tool permission.
    /// The Host obtains the manifest from encrypted scope, never from the worktree.
    /// </summary>
    public Task<SubagentChangeSet> CollectSubagentChangesAsync(SubagentCollectionSource source,
        long resultVersion, IReadOnlyDictionary<string, string> appliedBaseHashes, CancellationToken ct) =>
        UnderSubagentArtifactsAsync(() => CollectSubagentChangesCoreAsync(source, resultVersion, appliedBaseHashes, ct), ct);

    private async Task<SubagentChangeSet> CollectSubagentChangesCoreAsync(SubagentCollectionSource source,
        long resultVersion, IReadOnlyDictionary<string, string> appliedBaseHashes, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();
        var manifest = ValidateCollectionSource(source, resultVersion, appliedBaseHashes.Count);
        using var storage = DirectoryHandleLease.Open(source.StateDirectory, requireCanonicalPath: true);
        if (_session.Owns(source.InternalSessionId)) throw new AgentProtocolException("subagent_collection_busy");
        var result = await new SubagentChangeSetCollector().CollectAsync(manifest, resultVersion, ct, appliedBaseHashes).ConfigureAwait(false);
        // A legitimate continuation cannot open this session until publication
        // seals the previous result. Fail closed if that invariant was violated.
        if (_session.Owns(source.InternalSessionId)) throw new AgentProtocolException("subagent_collection_busy");
        return result;
    }

    private static SubagentWorkspaceManifest ValidateCollectionSource(SubagentCollectionSource source, long version, int bases)
    {
        try
        {
            if (!SubagentContractValidator.IsValidId(source.RootRunId) || !SubagentContractValidator.IsValidId(source.ChildRunId)
                || !SubagentContractValidator.IsValidId(source.AgentId) || string.IsNullOrEmpty(source.InternalSessionId)
                || source.InternalSessionId.Length > 128 || version < 1 || version > SubagentLimits.MaxAttempts
                || bases > SubagentLimits.MaxSnapshotFiles || string.IsNullOrWhiteSpace(source.WorktreeManifestJson)
                || source.WorktreeManifestJson.Length > 16 * 1024 * 1024)
                throw new AgentProtocolException("subagent_collection_source_invalid");
            var manifest = JsonSerializer.Deserialize<SubagentWorkspaceManifest>(source.WorktreeManifestJson)
                ?? throw new AgentProtocolException("subagent_collection_source_invalid");
            var snapshot = manifest.Snapshot;
            if (snapshot is null || !SubagentContractValidator.IsValidId(snapshot.SnapshotId)
                || !snapshot.SnapshotId.StartsWith("snapshot_", StringComparison.Ordinal)
                || manifest.AgentId != source.AgentId || snapshot.ParentRunId != source.RootRunId
                || !SamePath(snapshot.ParentRoot, source.ParentRoot) || !SamePath(manifest.WorkspaceRoot, source.WorkspaceRoot)
                || !SamePath(source.StateDirectory, Path.GetFullPath(source.StateDirectory))
                || DelegationPolicyEvaluator.Inside(source.ParentRoot, source.StateDirectory))
                throw new AgentProtocolException("subagent_collection_source_invalid");
            var owned = Path.Combine(source.StateDirectory, snapshot.SnapshotId);
            if (!SamePath(snapshot.BaseDirectory, Path.Combine(owned, "base"))
                || !SamePath(manifest.WorkspaceRoot, Path.Combine(owned, source.AgentId))
                || snapshot.Files is null || snapshot.Files.Count > SubagentLimits.MaxSnapshotFiles)
                throw new AgentProtocolException("subagent_collection_source_invalid");
            long length = 0;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in snapshot.Files)
            {
                if (file is null || string.IsNullOrWhiteSpace(file.RelativePath) || string.IsNullOrEmpty(file.Hash) || file.Length < 0 || file.Length > SubagentLimits.MaxSnapshotBytes
                    || !paths.Add(file.RelativePath) || Path.IsPathRooted(file.RelativePath)
                    || file.RelativePath.Split(['/', '\\']).Any(p => p is "" or "." or ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    || file.Mode is not (100644 or 100755)) throw new AgentProtocolException("subagent_collection_source_invalid");
                FileHashService.ValidateExpectedSha256(file.Hash);
                length += file.Length;
                if (length > SubagentLimits.MaxSnapshotBytes) throw new AgentProtocolException("subagent_collection_source_invalid");
            }
            return manifest;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new AgentProtocolException("subagent_collection_source_invalid"); }
    }

    private static bool SamePath(string left, string right) => Path.IsPathFullyQualified(left) && Path.IsPathFullyQualified(right)
        && string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);
}
