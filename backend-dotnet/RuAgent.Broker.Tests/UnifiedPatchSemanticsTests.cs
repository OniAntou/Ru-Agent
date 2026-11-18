using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class UnifiedPatchSemanticsTests
{
    [Theory]
    [InlineData("a\r\nb\r\n", "a\r\nc\r\n")]
    [InlineData("a\nb\n", "a\nc\n")]
    public void Preserves_line_endings_and_final_newline(string original, string expected)
    {
        var patch = UnifiedPatchParser.Parse("--- a/file\n+++ b/file\n@@ -2,1 +2,1 @@\n-b\n+c\n");
        Assert.Equal(expected, UnifiedPatchParser.ApplyHunks(original, patch.Single().Hunks));
    }

    [Fact]
    public void No_newline_marker_applies_to_new_side()
    {
        var patch = UnifiedPatchParser.Parse("--- a/file\n+++ b/file\n@@ -1,1 +1,1 @@\n-old\n+new\n\\ No newline at end of file\n");
        Assert.Equal("new", UnifiedPatchParser.ApplyHunks("old\n", patch.Single().Hunks));
    }

    [Theory]
    [InlineData("@@ -1,1 +1,2 @@\n-old\n+new\n")]
    [InlineData("@@ -9,0 +1,1 @@\n+new\n")]
    [InlineData("@@ -1,1 +9,1 @@\n-old\n+new\n")]
    public void Rejects_inconsistent_hunk_counts_and_ranges(string hunk)
    {
        var patch = UnifiedPatchParser.Parse("--- a/file\n+++ b/file\n" + hunk);
        Assert.Throws<ArgumentException>(() => UnifiedPatchParser.ApplyHunks("old", patch.Single().Hunks));
    }

    [Fact]
    public void Differing_header_paths_parse_as_a_rename_with_edits()
    {
        var file = UnifiedPatchParser.Parse("--- a/old.txt\n+++ b/new.txt\n@@ -1,1 +1,1 @@\n-a\n+b\n").Single();

        Assert.True(file.IsRename);
        Assert.Equal("old.txt", file.SourcePath);
        Assert.Equal("new.txt", file.TargetPath);
        Assert.Single(file.Hunks);
    }

    [Fact]
    public void Git_rename_lines_without_hunks_parse_as_a_pure_rename()
    {
        var file = UnifiedPatchParser.Parse(
            "diff --git a/old.txt b/new.txt\nsimilarity index 100%\nrename from old.txt\nrename to new.txt\n").Single();

        Assert.True(file.IsRename);
        Assert.Equal("old.txt", file.SourcePath);
        Assert.Equal("new.txt", file.TargetPath);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void Git_mode_and_similarity_lines_are_tolerated()
    {
        var files = UnifiedPatchParser.Parse(
            "diff --git a/n.txt b/n.txt\nnew file mode 100644\nindex 0000000..1111111\n--- /dev/null\n+++ b/n.txt\n@@ -0,0 +1,1 @@\n+x\n"
            + "diff --git a/r.txt b/s.txt\nsimilarity index 90%\nrename from r.txt\nrename to s.txt\n--- a/r.txt\n+++ b/s.txt\n@@ -1,1 +1,1 @@\n-a\n+b\n");

        Assert.True(files[0].IsNewFile);
        Assert.True(files[1].IsRename);
    }

    [Theory]
    [InlineData("diff --git a/a b/b\nrename from a\nrename to b\n--- a/a\n+++ b/c\n@@ -1,1 +1,1 @@\n-x\n+y\n", "invalid_patch_header")]
    [InlineData("diff --git a/a b/b\nrename from a\n", "invalid_patch_header")]
    [InlineData("diff --git a/a b/A\nrename from a\nrename to A\n", "patch_rename_case_only")]
    [InlineData("--- a/Readme.md\n+++ b/README.md\n@@ -1,1 +1,1 @@\n-x\n+y\n", "patch_rename_case_only")]
    [InlineData("--- a/a\n+++ b/b\n@@ -1,1 +1,1 @@\n-x\n+y\n--- a/a\n+++ b/a\n@@ -1,1 +1,1 @@\n-x\n+y\n", "patch_duplicate_path")]
    public void Inconsistent_or_colliding_renames_are_rejected(string patch, string code)
    {
        var error = Assert.Throws<ArgumentException>(() => UnifiedPatchParser.Parse(patch));
        Assert.Equal(code, error.Message);
    }

    [Fact]
    public void Zero_old_count_inserts_after_the_named_line()
    {
        var patch = UnifiedPatchParser.Parse("--- a/file\n+++ b/file\n@@ -1,0 +2,1 @@\n+inserted\n");
        Assert.Equal("first\ninserted\nlast\n", UnifiedPatchParser.ApplyHunks("first\nlast\n", patch.Single().Hunks));
    }
}
