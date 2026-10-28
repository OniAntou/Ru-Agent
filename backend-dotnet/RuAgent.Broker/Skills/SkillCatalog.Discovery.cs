namespace RuAgent.Broker.Skills;

public sealed partial class SkillCatalog
{
    private const int MaximumDiscoveryDepth = 6;
    private const int MaximumEnumeratedEntries = 4096;

    private sealed record RootScan(IReadOnlyList<string> Directories, IReadOnlyList<string> Packages,
        IReadOnlyList<SkillCatalogDiagnostic> Diagnostics, bool Truncated);

    // Breadth-first ordering preserves direct-child precedence while allowing
    // grouping folders. A directory with SKILL.md is a package, never a group.
    private static RootScan ScanRoot(string root, CancellationToken cancellationToken = default)
    {
        var directories = new List<string>();
        var packages = new List<string>();
        var diagnostics = new List<SkillCatalogDiagnostic>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var truncated = false;
        var enumerated = 0;
        while (pending.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasReparseAncestor(current.Path))
            {
                diagnostics.Add(new(current.Path, "reparse_point"));
                continue;
            }
            if (current.Depth > 0 && File.Exists(Path.Combine(current.Path, "SKILL.md")))
            {
                packages.Add(current.Path);
                continue;
            }
            try
            {
                var remaining = MaxDirectoriesPerRoot - directories.Count;
                // Retain the ordinal first candidates without buffering a whole
                // library or depending on filesystem enumeration order. The
                // separate enumeration cap bounds work for enormous libraries.
                var candidates = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var child in Directory.EnumerateDirectories(current.Path, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++enumerated > MaximumEnumeratedEntries)
                    {
                        truncated = true;
                        diagnostics.Add(new(current.Path, "scan_limit"));
                        break;
                    }
                    var name = Path.GetFileName(child);
                    if (name.StartsWith('.') || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    candidates.Add(child);
                    if (candidates.Count > remaining + 1) candidates.Remove(candidates.Max!);
                }
                var children = candidates.ToArray();
                if (current.Depth >= MaximumDiscoveryDepth)
                {
                    if (children.Length > 0)
                    {
                        diagnostics.Add(new(current.Path, "depth_limit"));
                        truncated = true;
                    }
                    continue;
                }
                if (children.Length > remaining)
                {
                    children = children.Take(remaining).ToArray();
                    diagnostics.Add(new(current.Path, "scan_limit"));
                    truncated = true;
                }
                foreach (var child in children)
                {
                    directories.Add(child);
                    pending.Enqueue((child, current.Depth + 1));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new(current.Path, "unreadable"));
            }
        }
        return new(directories, packages, diagnostics, truncated);
    }

    private static string? FindPackageDirectory(string root, string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var direct = Path.Combine(root, id);
        if (Directory.Exists(direct) && !HasReparseAncestor(direct)
            && File.Exists(Path.Combine(direct, "SKILL.md"))) return direct;
        return ScanRoot(root, cancellationToken).Packages.FirstOrDefault(directory =>
            string.Equals(Path.GetFileName(directory), id, StringComparison.OrdinalIgnoreCase));
    }
}
