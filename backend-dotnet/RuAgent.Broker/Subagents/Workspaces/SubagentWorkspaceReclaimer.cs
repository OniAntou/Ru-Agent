using RuAgent.Protocol;

namespace RuAgent.Broker.Subagents.Workspaces;

internal static class SubagentWorkspaceReclaimer
{
    public static void Reclaim(SubagentWorkspaceManifest manifest, string stateDirectory, CancellationToken ct)
    {
        var snapshot = manifest.Snapshot;
        if (string.IsNullOrEmpty(snapshot.OwnedDirectoryIdentity) || string.IsNullOrEmpty(snapshot.GitDirectoryIdentity)
            || string.IsNullOrEmpty(snapshot.CommonDirectoryIdentity) || string.IsNullOrEmpty(manifest.GitDirectoryIdentity)
            || !SamePath(manifest.CommonDirectory, snapshot.CommonDirectory)
            || !SamePath(Path.GetDirectoryName(manifest.GitDirectory)!, Path.Combine(snapshot.CommonDirectory, "worktrees"))
            || SamePath(manifest.GitDirectory, snapshot.GitDirectory))
            throw new AgentProtocolException("subagent_cleanup_identity_changed");

        WithAnchors(snapshot, stateDirectory, () =>
        {
            var owned = Path.GetDirectoryName(snapshot.BaseDirectory)!;
            OwnedDirectoryDeletion.Verify(owned, snapshot.OwnedDirectoryIdentity);
            OwnedDirectoryDeletion.Verify(manifest.WorkspaceRoot, manifest.DirectoryIdentity);
            OwnedDirectoryDeletion.Verify(manifest.GitDirectory, manifest.GitDirectoryIdentity);
            var deletion = new OwnedDirectoryDeletion();
            deletion.Delete(owned, snapshot.OwnedDirectoryIdentity, ct);
            // Retire only this captured Git administration entry, never global prune.
            deletion.Delete(manifest.GitDirectory, manifest.GitDirectoryIdentity, ct);
        });
    }

    public static void ReclaimSnapshot(SubagentSnapshot snapshot, string stateDirectory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(snapshot.OwnedDirectoryIdentity) || string.IsNullOrEmpty(snapshot.GitDirectoryIdentity)
            || string.IsNullOrEmpty(snapshot.CommonDirectoryIdentity))
            throw new AgentProtocolException("subagent_cleanup_identity_changed");
        WithAnchors(snapshot, stateDirectory, () =>
            new OwnedDirectoryDeletion().Delete(Path.GetDirectoryName(snapshot.BaseDirectory)!, snapshot.OwnedDirectoryIdentity, ct));
    }

    private static void WithAnchors(SubagentSnapshot snapshot, string stateDirectory, Action reclaim)
    {
        // Stable anchors do not grant access to parent worktree files or shared Git objects.
        using var parent = DirectoryHandleLease.Open(snapshot.ParentRoot, requireCanonicalPath: true);
        using var git = DirectoryHandleLease.Open(snapshot.GitDirectory, requireCanonicalPath: true);
        using var common = DirectoryHandleLease.Open(snapshot.CommonDirectory, requireCanonicalPath: true);
        using var storage = DirectoryHandleLease.Open(stateDirectory, requireCanonicalPath: true);
        if (parent.Identity != snapshot.ParentDirectoryIdentity || git.Identity != snapshot.GitDirectoryIdentity
            || common.Identity != snapshot.CommonDirectoryIdentity)
            throw new AgentProtocolException("subagent_cleanup_identity_changed");
        reclaim();
    }

    private static bool SamePath(string left, string right) => !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right) && Path.IsPathFullyQualified(left)
        && Path.IsPathFullyQualified(right) && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}
