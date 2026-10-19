using System.Text.Json;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Subagents.Workspaces;

public sealed partial class SubagentWorktreeProvisioner(SubagentGitAdapter? git = null)
{
    private readonly SubagentGitAdapter _git = git ?? new();

    private async Task<SubagentWorkspaceManifest> ProvisionCoreAsync(SubagentSnapshot snapshot, string agentId,
        Action<SubagentWorkspaceManifest> registered, CancellationToken ct, ISubagentWorkspaceAllocationObserver? observer)
    {
        if (!SubagentContractValidator.IsValidId(agentId)) throw new SubagentWorkspaceException("subagent_identity_invalid");
        using var parent = DirectoryHandleLease.Open(snapshot.ParentRoot, requireCanonicalPath: true);
        using var parentGit = DirectoryHandleLease.Open(snapshot.GitDirectory, requireCanonicalPath: true);
        using var common = DirectoryHandleLease.Open(snapshot.CommonDirectory, requireCanonicalPath: true);
        if (parent.Identity != snapshot.ParentDirectoryIdentity || parentGit.Identity != snapshot.GitDirectoryIdentity
            || common.Identity != snapshot.CommonDirectoryIdentity || await _git.GitDirectory(snapshot.ParentRoot, ct) != snapshot.GitDirectory
            || await _git.CommonDirectory(snapshot.ParentRoot, ct) != snapshot.CommonDirectory)
            throw new SubagentWorkspaceException("subagent_workspace_identity_changed");
        var directory = Path.GetDirectoryName(snapshot.BaseDirectory)!;
        using var owned = DirectoryHandleLease.Open(directory, requireCanonicalPath: true);
        if (owned.Identity != snapshot.OwnedDirectoryIdentity)
            throw new SubagentWorkspaceException("subagent_workspace_identity_changed");
        var child = Path.Combine(directory, agentId);
        if (Directory.Exists(child) || File.Exists(child)) throw new SubagentWorkspaceException("subagent_workspace_exists");
        await SubagentWorkspaceAllocationProgress.SnapshotAsync(observer, snapshot,
            SubagentWorkspaceAllocationStage.RegisteringWorktree, ct).ConfigureAwait(false);
        await _git.Add(snapshot.ParentRoot, child, snapshot.Head, ct).ConfigureAwait(false);
        using var childHandle = DirectoryHandleLease.Open(child, requireCanonicalPath: true);
        var childGitDirectory = await _git.GitDirectory(child, ct);
        using var gitHandle = DirectoryHandleLease.Open(childGitDirectory, requireCanonicalPath: true);
        var result = new SubagentWorkspaceManifest(agentId, snapshot, child, childHandle.Identity,
            childGitDirectory, await _git.CommonDirectory(child, ct), gitHandle.Identity);
        if (result.GitDirectory == snapshot.GitDirectory || result.CommonDirectory != snapshot.CommonDirectory)
            throw new SubagentWorkspaceException("subagent_workspace_identity_changed");
        registered(result);
        await SubagentWorkspaceAllocationProgress.WorktreeAsync(observer, result,
            SubagentWorkspaceAllocationStage.WorktreeRegistered, ct).ConfigureAwait(false);
        await _git.InitializeIndex(child, ct).ConfigureAwait(false);
        foreach (var file in snapshot.Files)
        {
            if (file.Hash == FileHashService.Missing) continue;
            var source = Path.GetFullPath(file.RelativePath, snapshot.BaseDirectory);
            var destination = Path.GetFullPath(file.RelativePath, child);
            if (!DelegationPolicyEvaluator.Inside(child, destination) || !DelegationPolicyEvaluator.Inside(snapshot.BaseDirectory, source))
                throw new SubagentWorkspaceException("subagent_snapshot_unsupported");
            if (FileHashService.ComputeFileHash(source) != file.Hash) throw new SubagentWorkspaceException("subagent_snapshot_changed");
            using var targetDirectory = DirectoryHandleLease.Open(Path.GetDirectoryName(destination)!, requireCanonicalPath: true, createMissing: true);
            File.Copy(source, destination, overwrite: false);
            if (FileHashService.ComputeFileHash(destination) != file.Hash) throw new SubagentWorkspaceException("subagent_snapshot_changed");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, agentId + ".json"), JsonSerializer.Serialize(result), ct).ConfigureAwait(false);
        await SubagentWorkspaceAllocationProgress.WorktreeAsync(observer, result,
            SubagentWorkspaceAllocationStage.Prepared, ct).ConfigureAwait(false);
        return result;
    }
}
