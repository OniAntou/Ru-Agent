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
        Action<SnapshotAllocation> allocated, CancellationToken ct, ISubagentWorkspaceAllocationObserver? observer)
    {
        if (parent.IsExpired(DateTimeOffset.UtcNow)) throw new SubagentWorkspaceException("subagent_parent_revoked");
        using var parentHandle = DirectoryHandleLease.Open(parent.CanonicalRoot, requireCanonicalPath: true);
        var root = parent.CanonicalRoot;
        if (!string.Equals(Path.GetFullPath(await _git.Root(root, ct)), root, StringComparison.OrdinalIgnoreCase))
            throw new SubagentWorkspaceException("subagent_git_root_required");
        if (DelegationPolicyEvaluator.Inside(root, Path.GetFullPath(stateDirectory)))
            throw new SubagentWorkspaceException("subagent_state_directory_invalid");
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
        using var stateHandle = DirectoryHandleLease.Open(directory, requireCanonicalPath: true, createMissing: true);
        allocated(new(directory, stateHandle.Identity));
        var baseDirectory = Path.Combine(directory, "base");
        await SubagentWorkspaceAllocationProgress.SnapshotAsync(observer, new(id, parent.RunId, root, head,
            gitDirectory, commonDirectory, parentHandle.Identity, baseDirectory, [], stateHandle.Identity,
            gitHandle.Identity, commonHandle.Identity), SubagentWorkspaceAllocationStage.SnapshotAllocated, ct).ConfigureAwait(false);
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
        if (head != await _git.Head(root, ct) || branch != await _git.Branch(root, ct)
            || index != await _git.Index(root, ct) || untracked != await _git.Untracked(root, ct)
            || gitDirectory != await _git.GitDirectory(root, ct) || commonDirectory != await _git.CommonDirectory(root, ct))
            throw new SubagentWorkspaceException("subagent_snapshot_changed");
        foreach (var file in files)
            if (FileHashService.ComputeFileHash(Path.GetFullPath(file.RelativePath, root)) != file.Hash)
                throw new SubagentWorkspaceException("subagent_snapshot_changed");
        var snapshot = new SubagentSnapshot(id, parent.RunId, root, head, gitDirectory, commonDirectory,
            parentHandle.Identity, baseDirectory, files.AsReadOnly(), stateHandle.Identity,
            gitHandle.Identity, commonHandle.Identity);
        await File.WriteAllTextAsync(Path.Combine(directory, "snapshot.json"), JsonSerializer.Serialize(snapshot), ct).ConfigureAwait(false);
        return snapshot;
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
