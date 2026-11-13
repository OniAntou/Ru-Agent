using RuAgent.Protocol.Subagents;
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

        Assert.Equal("--- a/src/Program.cs\n+++ b/src/Program.cs\n@@ -1,2 +1,2 @@\n one\n-two\n+TWO\n", patch);
    }

    [Fact]
    public void New_and_deleted_files_use_dev_null_headers()
    {
        var created = Assert.IsType<string>(SubagentPatchFormatter.Format("new.cs", null, "hello\n"));
        Assert.Equal("--- /dev/null\n+++ b/new.cs\n@@ -0,0 +1,1 @@\n+hello\n", created);
        var deleted = Assert.IsType<string>(SubagentPatchFormatter.Format("old.cs", "bye\n", null));
        Assert.Equal("--- a/old.cs\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-bye\n", deleted);
    }

    [Fact]
    public void Emptying_a_file_is_not_a_deletion()
    {
        // "" is an existing empty file; only null means the file is gone.
        var emptied = Assert.IsType<string>(SubagentPatchFormatter.Format("keep.cs", "bye\n", ""));
        Assert.StartsWith("--- a/keep.cs\n+++ b/keep.cs\n", emptied);
    }

    [Fact]
    public void Line_endings_and_a_missing_final_newline_survive_into_the_patch()
    {
        var crlf = Assert.IsType<string>(SubagentPatchFormatter.Format("win.txt", "one\r\ntwo\r\n", "one\r\nTWO\r\n"));
        Assert.Equal("--- a/win.txt\n+++ b/win.txt\n@@ -1,2 +1,2 @@\n one\r\n-two\r\n+TWO\r\n", crlf);

        var noNewline = Assert.IsType<string>(SubagentPatchFormatter.Format("tail.txt", "one\ntwo", "one\ntwo\nthree"));
        Assert.Equal("--- a/tail.txt\n+++ b/tail.txt\n@@ -1,2 +1,3 @@\n one\n-two\n\\ No newline at end of file\n"
            + "+two\n+three\n\\ No newline at end of file\n", noNewline);
    }

    [Fact]
    public void Distant_edits_become_separate_hunks_with_context()
    {
        var before = string.Concat(Enumerable.Range(1, 30).Select(n => $"line {n}\n"));
        var after = before.Replace("line 2\n", "LINE 2\n").Replace("line 28\n", "LINE 28\n");

        var diff = Assert.IsType<SubagentFileDiff>(SubagentDiff.Compute(before, after));

        Assert.Equal(2, diff.Hunks.Count);
        Assert.Equal((1, 5, 1, 5), (diff.Hunks[0].OldStart, diff.Hunks[0].OldCount, diff.Hunks[0].NewStart, diff.Hunks[0].NewCount));
        Assert.Equal((25, 6, 25, 6), (diff.Hunks[1].OldStart, diff.Hunks[1].OldCount, diff.Hunks[1].NewStart, diff.Hunks[1].NewCount));
        Assert.Equal((2, 2), (diff.Added, diff.Removed));
    }

    [Fact]
    public void A_small_edit_in_a_large_file_is_still_diffed_but_a_huge_rewrite_is_refused()
    {
        var before = string.Concat(Enumerable.Range(1, 6000).Select(n => $"line {n}\n"));
        var edited = Assert.IsType<SubagentFileDiff>(SubagentDiff.Compute(before, before.Replace("line 3000\n", "changed\n")));
        Assert.Equal((1, 1), (edited.Added, edited.Removed));

        var other = string.Concat(Enumerable.Range(1, 6000).Select(n => $"other {n}\n"));
        Assert.Null(SubagentDiff.Compute(before, other));
    }

    [Fact]
    public void Empty_or_oversize_or_invalid_paths_do_not_produce_a_patch()
    {
        Assert.Null(SubagentPatchFormatter.Format("same.cs", "x\n", "x\n"));
        Assert.Null(SubagentPatchFormatter.Format("bad\npath.cs", "x", "y"));
        Assert.Null(SubagentPatchFormatter.Format("big.cs", new string('x', SubagentPatchFormatter.MaxTextChars + 1), "y"));
        Assert.Null(SubagentPatchFormatter.Format("none.cs", null, null));
    }

    private static SubagentChangedFileResponse File(string status, string? @base, string? proposed,
        bool binary = false, bool truncated = false) =>
        new("src/a.txt", "base-hash", "proposed-hash", 0, status, binary, truncated, @base, proposed);

    [Fact]
    public void A_review_offers_a_patch_only_for_complete_text_against_the_captured_base()
    {
        var modified = SubagentChangeReview.File(File("base", "one\n", "two\n"));
        Assert.Equal((1, 1), (modified.Added, modified.Removed));
        Assert.NotNull(modified.Patch);

        // The Host sends no proposed text for a deletion: the patch must delete, not empty.
        var deleted = SubagentChangeReview.File(File("deleted", "one\n", null));
        Assert.Contains("+++ /dev/null\n", deleted.Patch);
        // An older Host sent "" there; the status decides, so it still deletes.
        Assert.Equal(deleted.Patch, SubagentChangeReview.File(File("deleted", "one\n", "")).Patch);

        // A binary proposal next to a text base used to look like a deletion patch.
        Assert.Null(SubagentChangeReview.File(File("base", "one\n", null, binary: true)).Patch);
        Assert.Null(SubagentChangeReview.File(File("base", "one\n", "tw", truncated: true)).Patch);
        foreach (var status in new[] { "applied", "diverged", "missing", "future_status" })
        {
            var review = SubagentChangeReview.File(File(status, null, "two\n"));
            Assert.Null(review.Patch);
            Assert.Null(review.Diff);
            Assert.NotNull(review.Note);
        }
    }

    [Fact]
    public void A_change_too_large_to_diff_says_so_instead_of_posing_as_a_diff()
    {
        var before = string.Concat(Enumerable.Range(1, 3000).Select(n => $"a{n}\n"));
        var after = string.Concat(Enumerable.Range(1, 3000).Select(n => $"b{n}\n"));

        var review = SubagentChangeReview.File(File("base", before, after));

        Assert.Null(review.Diff);
        Assert.Null(review.Patch);
        Assert.Equal(after, review.Fallback);
        Assert.Contains("showing the proposed content", review.Note);
    }

    [Fact]
    public void The_review_summary_counts_files_and_changed_lines()
    {
        var files = new[]
        {
            SubagentChangeReview.File(File("base", "one\n", "two\nthree\n")),
            SubagentChangeReview.File(File("applied", null, "x\n")),
        };

        Assert.Equal("2 files · +2 −1 · 1 without a diff", SubagentChangeReview.Summary(files));
    }
}
