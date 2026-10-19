using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Subagents.Integration;

public sealed class SubagentChangeSetCollector(SubagentGitAdapter? git = null)
{
    private readonly SubagentGitAdapter _git = git ?? new();

    public async Task<SubagentChangeSet> CollectAsync(SubagentWorkspaceManifest manifest, long resultVersion, CancellationToken ct,
        IReadOnlyDictionary<string, string>? appliedBaseHashes = null)
    {
        using var child = DirectoryHandleLease.Open(manifest.WorkspaceRoot, requireCanonicalPath: true);
        if (child.Identity != manifest.DirectoryIdentity)
            throw new SubagentWorkspaceException("subagent_workspace_identity_changed");
        await ValidateGitIdentityAsync(manifest, ct).ConfigureAwait(false);
        var index = await _git.Index(manifest.WorkspaceRoot, ct);
        var untracked = await _git.Untracked(manifest.WorkspaceRoot, ct);
        var current = SubagentSnapshotCapture.ParsePaths(index, untracked);
        var baseline = manifest.Snapshot.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        // Only the authority's durable apply receipts may supply these integration bases.
        // They do not change the frozen snapshot or import current parent edits into the child.
        var applied = new Dictionary<string, string>(appliedBaseHashes ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (path, hash) in applied)
        {
            if (Path.IsPathRooted(path) || path.Split(['/', '\\']).Any(p => p is "" or "." or ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                throw new SubagentWorkspaceException("subagent_snapshot_unsupported");
            FileHashService.ValidateExpectedSha256(hash);
        }
        var paths = baseline.Keys.Union(current.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(applied.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length > SubagentLimits.MaxSnapshotFiles) throw new SubagentWorkspaceException("subagent_snapshot_too_large");
        var changes = new List<SubagentFileChange>();
        var observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var relative in paths)
        {
            ct.ThrowIfCancellationRequested();
            baseline.TryGetValue(relative, out var original);
            var mode = current.TryGetValue(relative, out var entryMode) ? entryMode : original?.Mode ?? 100644;
            if (original is not null && original.Mode != mode) throw new SubagentWorkspaceException("subagent_mode_change_unsupported");
            var path = Path.GetFullPath(relative, manifest.WorkspaceRoot);
            var resolved = new CanonicalPathResolver(manifest.WorkspaceRoot).Resolve(relative);
            if (!DelegationPolicyEvaluator.Inside(manifest.WorkspaceRoot, resolved.CanonicalPath)
                || !string.Equals(path, resolved.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                throw new SubagentWorkspaceException("subagent_snapshot_unsupported");
            var beforeHash = applied.TryGetValue(relative, out var appliedHash) ? appliedHash : original?.Hash ?? FileHashService.Missing;
            var afterHash = FileHashService.Missing;
            byte[] bytes = [];
            if (File.Exists(path))
            {
                using var directory = DirectoryHandleLease.Open(Path.GetDirectoryName(path)!, requireCanonicalPath: true);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new SubagentWorkspaceException("subagent_snapshot_unsupported");
                using var input = HardLinkGuard.OpenRead(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                total += input.Length;
                if (total > SubagentLimits.MaxSnapshotBytes) throw new SubagentWorkspaceException("subagent_snapshot_too_large");
                bytes = new byte[checked((int)input.Length)];
                await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                afterHash = FileHashService.ComputeBytesHash(bytes);
            }
            else if (Directory.Exists(path)) throw new SubagentWorkspaceException("subagent_snapshot_unsupported");
            observed.Add(relative, afterHash);
            if (beforeHash != afterHash) changes.Add(new(relative, beforeHash, afterHash, bytes, mode));
        }
        if (index != await _git.Index(manifest.WorkspaceRoot, ct) || untracked != await _git.Untracked(manifest.WorkspaceRoot, ct))
            throw new SubagentWorkspaceException("subagent_snapshot_changed");
        await ValidateGitIdentityAsync(manifest, ct).ConfigureAwait(false);
        // An unchanged file can become a delta after its first read. Checking only
        // emitted changes would silently seal an incomplete result in that race.
        foreach (var (relative, hash) in observed)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(relative, manifest.WorkspaceRoot);
            if (Directory.Exists(path) || FileHashService.ComputeFileHash(path) != hash)
                throw new SubagentWorkspaceException("subagent_snapshot_changed");
        }
        return new(manifest.AgentId, manifest.Snapshot.ParentRunId, manifest.Snapshot.ParentRoot,
            manifest.Snapshot.SnapshotId, resultVersion, changes.AsReadOnly());
    }

    private async Task ValidateGitIdentityAsync(SubagentWorkspaceManifest manifest, CancellationToken ct)
    {
        if (await _git.GitDirectory(manifest.WorkspaceRoot, ct) != manifest.GitDirectory
            || await _git.CommonDirectory(manifest.WorkspaceRoot, ct) != manifest.CommonDirectory
            || !string.Equals(Path.GetFullPath(await _git.Root(manifest.WorkspaceRoot, ct)), manifest.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
            throw new SubagentWorkspaceException("subagent_workspace_identity_changed");
    }
}
