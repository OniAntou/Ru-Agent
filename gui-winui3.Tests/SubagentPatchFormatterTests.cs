using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SubagentPatchFormatterTests
{
    [Fact]
    public void A_modified_file_produces_a_full_unified_patch()
    {
        var patch = Assert.IsType<string>(SubagentPatchFormatter.Format(
            "src/Program.cs", "one\ntwo\n", "one\nTWO\n"));

        Assert.True(patch.StartsWith("--- a/src/Program.cs\n+++ b/src/Program.cs\n@@ -1,2 +1,2 @@\n", StringComparison.Ordinal), patch);
        Assert.Contains("\n one\n", patch);
        Assert.Contains("\n-two\n", patch);
        Assert.Contains("\n+TWO\n", patch);
    }

    [Fact]
    public void New_and_deleted_files_use_dev_null_headers()
    {
        var created = Assert.IsType<string>(SubagentPatchFormatter.Format("new.cs", null, "hello\n"));
        Assert.StartsWith("--- /dev/null\n+++ b/new.cs\n@@ -0,0 +1,1 @@\n+hello\n", created);
        var deleted = Assert.IsType<string>(SubagentPatchFormatter.Format("old.cs", "bye\n", null));
        Assert.StartsWith("--- a/old.cs\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-bye\n", deleted);
    }

    [Fact]
    public void Empty_or_oversize_or_invalid_paths_do_not_produce_a_patch()
    {
        Assert.Null(SubagentPatchFormatter.Format("same.cs", "x\n", "x\n"));
        Assert.Null(SubagentPatchFormatter.Format("bad\npath.cs", "x", "y"));
        Assert.Null(SubagentPatchFormatter.Format("big.cs", new string('x', SubagentPatchFormatter.MaxTextChars + 1), "y"));
        Assert.Null(SubagentPatchFormatter.Format("none.cs", null, null));
    }
}
