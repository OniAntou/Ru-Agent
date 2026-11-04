using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Subagents.Workspaces;

/// <summary>Deletes one proven owned tree without following reparse points or issuing recursive path deletes.</summary>
internal sealed class OwnedDirectoryDeletion
{
    private const int MaxDepth = 128;
    private const int MaxEntries = SubagentLimits.MaxSnapshotFiles * 4;
    private int _entries;

    public void Delete(string path, string identity, CancellationToken ct) => DeleteCore(path, identity, 0, ct);

    private void DeleteCore(string path, string? identity, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > MaxDepth) throw new SubagentWorkspaceException("subagent_cleanup_too_large");
        if (!Exists(path)) return;
        // The caller holds the configured storage/repository anchors. Each
        // nested canonical directory remains held while its children are removed.
        using (var held = DirectoryHandleLease.Open(path, requireCanonicalPath: true))
        {
            if (identity is not null && held.Identity != identity)
                throw new AgentProtocolException("subagent_cleanup_identity_changed");
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                ct.ThrowIfCancellationRequested();
                if (++_entries > MaxEntries) throw new SubagentWorkspaceException("subagent_cleanup_too_large");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) == 0) File.Delete(entry);
                else if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry, recursive: false);
                else DeleteCore(entry, null, depth + 1, ct);
            }
        }
        // Never recursively delete after releasing the directory identity. A
        // replacement with contents fails here; a link is unlinked, not traversed.
        Directory.Delete(path, recursive: false);
    }

    /// <summary>
    /// Makes a Ru-owned private base repository (ADR 0042) deletable. Git writes its
    /// object files read-only; the general deletion treats a read-only file as a reason
    /// to stop, which is right for anything else in owned storage. Links are not followed.
    /// </summary>
    public static void ReleasePrivateRepository(string ownedDirectory, CancellationToken ct)
    {
        var repository = Path.Combine(ownedDirectory, SubagentSnapshotCapture.PrivateRepositoryName);
        if (!Exists(repository)) return;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((repository, 0));
        var entries = 0;
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            if (depth > MaxDepth) throw new SubagentWorkspaceException("subagent_cleanup_too_large");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > MaxEntries) throw new SubagentWorkspaceException("subagent_cleanup_too_large");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((entry, depth + 1));
                else if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    public static bool Exists(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
                throw new AgentProtocolException("subagent_cleanup_identity_changed");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static void Verify(string path, string identity)
    {
        if (!Exists(path)) return;
        using var held = DirectoryHandleLease.Open(path, requireCanonicalPath: true);
        if (held.Identity != identity) throw new AgentProtocolException("subagent_cleanup_identity_changed");
    }
}
