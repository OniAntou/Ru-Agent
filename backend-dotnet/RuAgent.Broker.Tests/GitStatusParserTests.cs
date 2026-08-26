using RuAgent.Broker.Tools.Git;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class GitStatusParserTests
{
    private const string OidA = "1234567890abcdef1234567890abcdef12345678";
    private const string OidB = "89abcdef89abcdef89abcdef89abcdef89abcdef";

    [Fact]
    public void Empty_status_text_parses_zero_entries_without_errors()
    {
        var result = GitStatusParser.Parse(string.Empty);

        Assert.Empty(result.Entries);
        Assert.Empty(result.Errors);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Branch_header_fields_are_parsed()
    {
        var text = string.Join('\n',
            $"# branch.oid {OidA}",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -1");

        var result = GitStatusParser.Parse(text);

        Assert.Equal(OidA, result.Branch.HeadOid);
        Assert.Equal("main", result.Branch.HeadRef);
        Assert.False(result.Branch.IsDetached);
        Assert.Equal("origin/main", result.Branch.Upstream);
        Assert.Equal(2, result.Branch.AheadCount);
        Assert.Equal(1, result.Branch.BehindCount);
    }

    [Fact]
    public void Detached_head_header_is_detected()
    {
        var text = string.Join('\n',
            $"# branch.oid {OidA}",
            "# branch.head (detached)");

        var result = GitStatusParser.Parse(text);

        Assert.True(result.Branch.IsDetached);
        Assert.Equal("(detached)", result.Branch.HeadRef);
    }

    [Fact]
    public void Initial_branch_oid_placeholder_becomes_null_head()
    {
        var text = "# branch.oid (initial)\n# branch.head main";

        var result = GitStatusParser.Parse(text);

        Assert.Null(result.Branch.HeadOid);
    }

    [Fact]
    public void Staged_unstaged_and_mixed_codes_split_correctly()
    {
        var stagedOnly = $"1 M. N... 100644 100644 100644 {OidA} {OidB} staged-only.txt";
        var unstagedOnly = $"1 .M N... 100644 100644 100644 {OidA} {OidB} unstaged-only.txt";
        var mixed = $"1 MM N... 100644 100644 100644 {OidA} {OidB} mixed.txt";

        var result = GitStatusParser.Parse(
            string.Join('\n', "# branch.head main", stagedOnly, unstagedOnly, mixed));

        Assert.Equal(3, result.Entries.Count);

        var staged = result.Entries.Single(e => e.Path == "staged-only.txt");
        Assert.True(staged.HasStagedChanges);
        Assert.False(staged.HasUnstagedChanges);
        Assert.Equal(RunGitEntryKind.Tracked, staged.Kind);
        Assert.False(staged.IsSubmodule);

        var unstaged = result.Entries.Single(e => e.Path == "unstaged-only.txt");
        Assert.False(unstaged.HasStagedChanges);
        Assert.True(unstaged.HasUnstagedChanges);

        var both = result.Entries.Single(e => e.Path == "mixed.txt");
        Assert.True(both.HasStagedChanges);
        Assert.True(both.HasUnstagedChanges);
    }

    [Fact]
    public void Renamed_entry_captures_original_path_and_score()
    {
        var line = $"2 R. N... 100644 100644 100644 {OidA} {OidB} R100 renamed-new.txt\trenamed-old.txt";

        var result = GitStatusParser.Parse(line);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("renamed-new.txt", entry.Path);
        Assert.Equal("renamed-old.txt", entry.OriginalPath);
        Assert.Equal(100, entry.RenameScore);
        Assert.True(entry.HasStagedChanges);
    }

    [Fact]
    public void Copied_entry_score_is_parsed()
    {
        var line = $"2 C. N... 100644 100644 100644 {OidA} {OidB} C75 copied.txt\toriginal.txt";

        var result = GitStatusParser.Parse(line);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("copied.txt", entry.Path);
        Assert.Equal("original.txt", entry.OriginalPath);
        Assert.Equal(75, entry.RenameScore);
    }

    [Fact]
    public void Untracked_entries_use_the_untracked_kind()
    {
        var result = GitStatusParser.Parse("? new-file.txt");

        var entry = Assert.Single(result.Entries);
        Assert.Equal(RunGitEntryKind.Untracked, entry.Kind);
        Assert.Equal("new-file.txt", entry.Path);
        Assert.False(entry.HasStagedChanges);
        Assert.False(entry.HasUnstagedChanges);
    }

    [Fact]
    public void Ignored_lines_are_skipped_silently()
    {
        var result = GitStatusParser.Parse("! build-output.bin");

        Assert.Empty(result.Entries);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Unmerged_entries_parse_with_submodule_flag()
    {
        var unmerged = $"u UU N... 100644 100644 100644 100644 {OidA} {OidB} {OidA} conflicted.txt";

        var result = GitStatusParser.Parse(unmerged);

        Assert.Empty(result.Errors);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(RunGitEntryKind.Unmerged, entry.Kind);
        Assert.Equal("conflicted.txt", entry.Path);
    }

    [Fact]
    public void Submodule_entries_are_flagged()
    {
        var line = $"1 .M S... 160000 160000 160000 {OidA} {OidB} vendor-lib";

        var result = GitStatusParser.Parse(line);

        var entry = Assert.Single(result.Entries);
        Assert.True(entry.IsSubmodule);
        Assert.Equal("vendor-lib", entry.Path);
    }

    [Fact]
    public void Paths_with_plain_spaces_are_kept_intact()
    {
        var line = $"1 .M N... 100644 100644 100644 {OidA} {OidB} file with spaces.txt";

        var result = GitStatusParser.Parse(line);

        Assert.Equal("file with spaces.txt", Assert.Single(result.Entries).Path);
    }

    [Theory]
    [InlineData("tab\\there.txt", "tab\there.txt")]
    [InlineData("quo\\\"te.txt", "quo\"te.txt")]
    [InlineData("back\\\\slash.txt", "back\\slash.txt")]
    public void Quoted_paths_are_unescaped(string encoded, string decoded)
    {
        var line = $"1 .M N... 100644 100644 100644 {OidA} {OidB} \"{encoded}\"";

        var result = GitStatusParser.Parse(line);

        Assert.Equal(decoded, Assert.Single(result.Entries).Path);
    }

    [Fact]
    public void Octal_utf8_escapes_decode_to_unicode_characters()
    {
        var line = $"1 .M N... 100644 100644 100644 {OidA} {OidB} \"caf\\303\\251.txt\"";

        var result = GitStatusParser.Parse(line);

        Assert.Equal("café.txt", Assert.Single(result.Entries).Path);
    }

    [Fact]
    public void Quoted_rename_containing_arrow_separator_stays_intact()
    {
        var line = $"2 R. N... 100644 100644 100644 {OidA} {OidB} R100 \"a -> b.txt\"\t\"old -> name.txt\"";

        var result = GitStatusParser.Parse(line);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("a -> b.txt", entry.Path);
        Assert.Equal("old -> name.txt", entry.OriginalPath);
    }

    [Fact]
    public void Malformed_lines_are_reported_without_aborting_remaining_entries()
    {
        var good = $"1 .M N... 100644 100644 100644 {OidA} {OidB} good.txt";
        var text = string.Join('\n', "zzz garbage-line", good, "? also-fine.txt");

        var result = GitStatusParser.Parse(text);

        Assert.Equal(2, result.Entries.Count);
        Assert.Contains(result.Errors, e => e.Contains("zzz garbage-line", StringComparison.Ordinal));
    }

    [Fact]
    public void Entry_cap_marks_result_truncated()
    {
        var lines = new List<string> { "# branch.head main" };
        for (var i = 0; i < GitStatusParser.MaxEntries + 50; i++)
        {
            lines.Add($"? untracked-{i}.txt");
        }

        var result = GitStatusParser.Parse(string.Join('\n', lines));

        Assert.Equal(GitStatusParser.MaxEntries, result.Entries.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Windows_line_endings_are_tolerated()
    {
        var text = $"# branch.head main\r\n? a.txt\r\n? b.txt\r\n";

        var result = GitStatusParser.Parse(text);

        Assert.Equal(2, result.Entries.Count);
        Assert.Empty(result.Errors);
    }
}
