using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class FileTreeSnapshotLoaderTests
{
    [Fact]
    public void Refresh_reloads_only_expanded_branches_and_removes_deleted_entries()
    {
        string root = Path.Combine(Path.GetTempPath(), "ru-tree-refresh-" + Guid.NewGuid().ToString("N"));
        string expanded = Path.Combine(root, "open");
        try
        {
            Directory.CreateDirectory(expanded);
            Directory.CreateDirectory(Path.Combine(root, "closed"));
            File.WriteAllText(Path.Combine(expanded, "current.txt"), "test");
            File.WriteAllText(Path.Combine(root, "closed", "hidden.txt"), "test");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { expanded, Path.Combine(root, "deleted") };
            var result = FileTreeSnapshotLoader.Load(root, paths);
            Assert.Equal(["closed", "open", "current.txt"], result.Rows.Select(row => row.Name));
            Assert.True(result.Rows[1].IsExpanded);
            Assert.Equal(1, result.Rows[2].Depth);
            File.Delete(Path.Combine(expanded, "current.txt"));
            Assert.Equal(["closed", "open"], FileTreeSnapshotLoader.Load(root, paths).Rows.Select(row => row.Name));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Expanded_tree_stays_bounded_and_does_not_expose_skipped_directories()
    {
        string root = Path.Combine(Path.GetTempPath(), "ru-tree-cap-" + Guid.NewGuid().ToString("N"));
        try
        {
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int dir = 0; dir < 7; dir++)
            {
                string path = Path.Combine(root, "branch" + dir);
                Directory.CreateDirectory(path);
                expanded.Add(path);
                for (int file = 0; file < 230; file++) File.WriteAllText(Path.Combine(path, file + ".txt"), "");
            }
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            expanded.Add(Path.Combine(root, ".git"));
            var result = FileTreeSnapshotLoader.Load(root, expanded);
            Assert.True(result.Truncated);
            Assert.Equal(1500, result.Rows.Count);
            Assert.DoesNotContain(result.Rows, row => row.Name == ".git");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
