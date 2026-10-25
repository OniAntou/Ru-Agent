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
    public void Zero_old_count_inserts_after_the_named_line()
    {
        var patch = UnifiedPatchParser.Parse("--- a/file\n+++ b/file\n@@ -1,0 +2,1 @@\n+inserted\n");
        Assert.Equal("first\ninserted\nlast\n", UnifiedPatchParser.ApplyHunks("first\nlast\n", patch.Single().Hunks));
    }
}
