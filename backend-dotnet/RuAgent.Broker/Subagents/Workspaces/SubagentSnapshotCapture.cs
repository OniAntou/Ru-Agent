using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Subagents.Workspaces;

public sealed partial class SubagentSnapshotCapture(SubagentGitAdapter? git = null)
{
    private readonly SubagentGitAdapter _git = git ?? new();

    private async Task<SubagentSnapshot> CaptureCoreAsync(BrokerRunLease parent, string stateDirectory,
        Action allocating, Action<SnapshotAllocation> allocated, CancellationToken ct, ISubagentWorkspaceAllocationObserver? observer)
    {
        if (parent.IsExpired(DateTimeOffset.UtcNow)) throw new SubagentWorkspaceException("subagent_parent_revoked");
        using var parentHandle = DirectoryHandleLease.Open(parent.CanonicalRoot, requireCanonicalPath: true);
        var root = parent.CanonicalRoot;
        if (DelegationPolicyEvaluator.Inside(root, Path.GetFullPath(stateDirectory)))
            throw new SubagentWorkspaceException("subagent_state_directory_invalid");
        return await UsesParentRepositoryAsync(root, ct).ConfigureAwait(false)
            ? await CaptureFromParentRepositoryAsync(parent, parentHandle, stateDirectory, allocating, allocated, ct, observer).ConfigureAwait(false)
            : await CaptureIntoPrivateRepositoryAsync(parent, parentHandle, stateDirectory, allocating, allocated, ct, observer).ConfigureAwait(false);
    }

    /// <summary>
    /// The original path: a workspace that is its repository's root with a commit
    /// registers the child worktree in that repository. Anything else (no repository,
    /// a workspace below the root, or no commit yet) uses a private base repository.
    /// </summary>
    private async Task<bool> UsesParentRepositoryAsync(string root, CancellationToken ct)
    {
        string toplevel;
        try { toplevel = await _git.Root(root, ct).ConfigureAwait(false); }
        catch (SubagentWorkspaceException error) when (error.Code == "subagent_git_root_unavailable") { return false; }
        if (!string.Equals(Path.GetFullPath(toplevel), root, StringComparison.OrdinalIgnoreCase)) return false;
        try { _ = await _git.Head(root, ct).ConfigureAwait(false); }
        catch (SubagentWorkspaceException error) when (error.Code == "subagent_git_head_unavailable") { return false; }
        return true;
    }

    private async Task<SubagentSnapshot> CaptureFromParentRepositoryAsync(BrokerRunLease parent, DirectoryHandleLease parentHandle,
        string stateDirectory, Action allocating, Action<SnapshotAllocation> allocated, CancellationToken ct,
        ISubagentWorkspaceAllocationObserver? observer)
    {
        var root = parent.CanonicalRoot;
        if (await _git.Sparse(root, ct) == "true") throw new SubagentWorkspaceException("subagent_snapshot_unsupported");
        var head = await _git.Head(root, ct);
        var branch = await _git.Branch(root, ct);
        var gitDirectory = await _git.GitDirectory(root, ct);
        var commonDirectory = await _git.CommonDirectory(root, ct);
        using var gitHandle = DirectoryHandleLease.Open(gitDirectory, requireCanonicalPath: true);
        using var commonHandle = DirectoryHandleLease.Open(commonDirectory, requireCanonicalPath: true);
        var index = await _git.Index(root, ct);
        var untracked = await _git.Untracked(root, ct);
        var paths = ParsePaths(index, untracked);
        var id = observer?.Reservation.SnapshotId ?? "snapshot_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetFullPath(stateDirectory), id);
        allocating();
        using var stateHandle = DirectoryHandleLease.Open(directory, requireCanonicalPath: true, createMissing: true);
        allocated(new(directory, stateHandle.Identity));
        var baseDirectory = Path.Combine(directory, "base");
        await SubagentWorkspaceAllocationProgress.SnapshotAsync(observer, new(id, parent.RunId, root, head,
            gitDirectory, commonDirectory, parentHandle.Identity, baseDirectory, [], stateHandle.Identity,
            gitHandle.Identity, commonHandle.Identity), SubagentWorkspaceAllocationStage.SnapshotAllocated, ct).ConfigureAwait(false);
        var files = await CopyBaseAsync(parent, paths, baseDirectory, ct).ConfigureAwait(false);
        if (head != await _git.Head(root, ct) || branch != await _git.Branch(root, ct)
            || index != await _git.Index(root, ct) || untracked != await _git.Untracked(root, ct)
            || gitDirectory != await _git.GitDirectory(root, ct) || commonDirectory != await _git.CommonDirectory(root, ct))
            throw new SubagentWorkspaceException("subagent_snapshot_changed");
        VerifyUnchanged(root, files);
        var snapshot = new SubagentSnapshot(id, parent.RunId, root, head, gitDirectory, commonDirectory,
            parentHandle.Identity, baseDirectory, files.AsReadOnly(), stateHandle.Identity,
            gitHandle.Identity, commonHandle.Identity);
        await File.WriteAllTextAsync(Path.Combine(directory, "snapshot.json"), JsonSerializer.Serialize(snapshot), ct).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>
    /// ADR 0042: the workspace is only ever a read-only work tree. The repository that
    /// holds the base commit, and later the child's worktree registration, lives in the
    /// snapshot's own Ru-owned directory and is deleted with it.
    /// </summary>
    private async Task<SubagentSnapshot> CaptureIntoPrivateRepositoryAsync(BrokerRunLease parent, DirectoryHandleLease parentHandle,
        string stateDirectory, Action allocating, Action<SnapshotAllocation> allocated, CancellationToken ct,
        ISubagentWorkspaceAllocationObserver? observer)
    {
        var root = parent.CanonicalRoot;
        var id = observer?.Reservation.SnapshotId ?? "snapshot_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetFullPath(stateDirectory), id);
        allocating();
        using var stateHandle = DirectoryHandleLease.Open(directory, requireCanonicalPath: true, createMissing: true);
        allocated(new(directory, stateHandle.Identity));
        var repository = Path.Combine(directory, PrivateRepositoryName);
        await _git.InitializePrivate(directory, repository, ct).ConfigureAwait(false);
        var gitDirectory = await _git.GitDirectory(repository, ct);
        var commonDirectory = await _git.CommonDirectory(repository, ct);
        if (!DelegationPolicyEvaluator.Inside(directory, gitDirectory) || !DelegationPolicyEvaluator.Inside(directory, commonDirectory))
            throw new SubagentWorkspaceException("subagent_workspace_identity_changed");
        // The commit must hold the captured bytes exactly: no text, eol or filter
        // conversion from attributes the workspace happens to carry.
        Directory.CreateDirectory(Path.Combine(gitDirectory, "info"));
        await File.WriteAllTextAsync(Path.Combine(gitDirectory, "info", "attributes"),
            "* -text -eol -filter -ident -working-tree-encoding\n", ct).ConfigureAwait(false);
        using var gitHandle = DirectoryHandleLease.Open(gitDirectory, requireCanonicalPath: true);
        using var commonHandle = DirectoryHandleLease.Open(commonDirectory, requireCanonicalPath: true);
        var baseDirectory = Path.Combine(directory, "base");
        // Evidence must not change between stages, and the base commit only exists after
        // copying, so the snapshot names the private branch. The snapshot owns exactly one
        // child, provisioned before it runs, so nothing can move the branch in between.
        await SubagentWorkspaceAllocationProgress.SnapshotAsync(observer, new(id, parent.RunId, root, PrivateBaseRef,
            gitDirectory, commonDirectory, parentHandle.Identity, baseDirectory, [], stateHandle.Identity,
            gitHandle.Identity, commonHandle.Identity, repository), SubagentWorkspaceAllocationStage.SnapshotAllocated, ct).ConfigureAwait(false);
        var untracked = await _git.UntrackedIn(gitDirectory, root, ct);
        var files = await CopyBaseAsync(parent, ParsePaths("", untracked), baseDirectory, ct).ConfigureAwait(false);
        if (untracked != await _git.UntrackedIn(gitDirectory, root, ct))
            throw new SubagentWorkspaceException("subagent_snapshot_changed");
        VerifyUnchanged(root, files);
        await _git.CommitBase(gitDirectory, baseDirectory, ct).ConfigureAwait(false);
        _ = await _git.Head(repository, ct);
        var snapshot = new SubagentSnapshot(id, parent.RunId, root, PrivateBaseRef, gitDirectory, commonDirectory,
            parentHandle.Identity, baseDirectory, files.AsReadOnly(), stateHandle.Identity,
            gitHandle.Identity, commonHandle.Identity, repository);
        await File.WriteAllTextAsync(Path.Combine(directory, "snapshot.json"), JsonSerializer.Serialize(snapshot), ct).ConfigureAwait(false);
        return snapshot;
    }

    internal const string PrivateRepositoryName = "repository";
    internal const string PrivateBaseRef = "refs/heads/ru-base";

    /// <summary>Copies each captured path into the base, under the parent's read policy and the snapshot bounds.</summary>
    private static async Task<List<SubagentSnapshotFile>> CopyBaseAsync(BrokerRunLease parent, Dictionary<string, int> paths,
        string baseDirectory, CancellationToken ct)
    {
        var root = parent.CanonicalRoot;
        Directory.CreateDirectory(baseDirectory);
        var files = new List<SubagentSnapshotFile>();
        long total = 0;
        foreach (var (relative, mode) in paths.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var context = new PolicyContextFactory(parent).Create("read", "1.0.0", new JsonObject { ["path"] = relative });
            var policy = new PolicySnapshotEvaluator();
            if (context.Target is not { } target || !DelegationPolicyEvaluator.Inside(root, target.CanonicalPath)
                || policy.Evaluate(parent.Snapshot, context, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow).Decision != "allow"
                || policy.Evaluate(parent.Snapshot, context with { RelativePathInsideWorkspace = null }, PolicyRequestInvariants.Valid, DateTimeOffset.UtcNow).Decision != "allow")
                throw new SubagentWorkspaceException("subagent_snapshot_read_denied");
            var fullPath = Path.GetFullPath(relative, root);
            if (!File.Exists(fullPath))
            {
                if (Directory.Exists(fullPath)) throw new SubagentWorkspaceException("subagent_snapshot_unsupported");
                files.Add(new(relative, FileHashService.Missing, mode, 0));
                continue;
            }
            using var directoryHandle = DirectoryHandleLease.Open(Path.GetDirectoryName(fullPath)!, requireCanonicalPath: true);
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new SubagentWorkspaceException("subagent_snapshot_unsupported");
            using var input = HardLinkGuard.OpenRead(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            total = checked(total + input.Length);
            if (total > SubagentLimits.MaxSnapshotBytes) throw new SubagentWorkspaceException("subagent_snapshot_too_large");
            var bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            var destination = Path.GetFullPath(relative, baseDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, bytes, ct).ConfigureAwait(false);
            files.Add(new(relative, FileHashService.ComputeBytesHash(bytes), mode, bytes.Length));
        }

        return files;
    }

    private static void VerifyUnchanged(string root, IEnumerable<SubagentSnapshotFile> files)
    {
        foreach (var file in files)
            if (FileHashService.ComputeFileHash(Path.GetFullPath(file.RelativePath, root)) != file.Hash)
                throw new SubagentWorkspaceException("subagent_snapshot_changed");
    }

    internal static Dictionary<string, int> ParsePaths(string index, string untracked)
    {
        var paths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0) throw new SubagentWorkspaceException("subagent_snapshot_unsupported");
            var metadata = entry[..tab].Split(' ');
            if (metadata.Length != 3 || metadata[2] != "0" || metadata[0] is not ("100644" or "100755"))
                throw new SubagentWorkspaceException("subagent_snapshot_unsupported");
            Add(entry[(tab + 1)..], int.Parse(metadata[0]));
        }
        foreach (var path in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries)) Add(path, 100644);
        return paths;

        void Add(string path, int mode)
        {
            if (Path.IsPathRooted(path) || path.Split('/').Any(p => p is "" or "." or ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase))
                || path.Contains('\\') || path.Contains(':') || !paths.TryAdd(path, mode))
                throw new SubagentWorkspaceException("subagent_snapshot_unsupported");
            if (paths.Count > SubagentLimits.MaxSnapshotFiles) throw new SubagentWorkspaceException("subagent_snapshot_too_large");
        }
    }
}
