using RuApp.Models;
using Xunit;

namespace RuApp.Tests
{
    public sealed class DiffEngineTests
    {
        [Fact]
        public void Compute_handles_empty_inputs()
        {
            var emptyResult = DiffEngine.Compute(string.Empty, string.Empty);
            Assert.Empty(emptyResult);

            var nullResult = DiffEngine.Compute(null, null);
            Assert.Empty(nullResult);
        }

        [Fact]
        public void Compute_handles_pure_addition()
        {
            var result = DiffEngine.Compute(string.Empty, "line 1\nline 2", startLine: 10);
            Assert.Equal(2, result.Count);
            Assert.All(result, line => Assert.True(line.IsAdded));
            Assert.Equal("10", result[0].DisplayLineNumber);
            Assert.Equal("line 1", result[0].Text);
            Assert.Equal("11", result[1].DisplayLineNumber);
            Assert.Equal("line 2", result[1].Text);
        }

        [Fact]
        public void Compute_handles_pure_removal()
        {
            var result = DiffEngine.Compute("alpha\nbeta", string.Empty, startLine: 5);
            Assert.Equal(2, result.Count);
            Assert.All(result, line => Assert.True(line.IsRemoved));
            Assert.Equal("5", result[0].DisplayLineNumber);
            Assert.Equal("alpha", result[0].Text);
            Assert.Equal("6", result[1].DisplayLineNumber);
            Assert.Equal("beta", result[1].Text);
        }

        [Theory]
        [InlineData(1, "a\nb", "a\nc", "@@ -1,2 +1,2 @@")]
        [InlineData(7, "a", "b", "@@ -7 +7 @@")]
        [InlineData(42, "", "only", "@@ -41,0 +42 @@")]
        [InlineData(5, "alpha\nbeta", "", "@@ -5,2 +4,0 @@")]
        public void Header_names_both_sides_of_a_fragment_like_git(int startLine, string before, string after, string expected)
        {
            var lines = DiffEngine.Compute(before, after, startLine);

            Assert.Equal(expected, DiffEngine.Header(lines, startLine));
        }

        [Fact]
        public void Locate_finds_a_fragment_in_the_file_and_prefers_the_reported_line()
        {
            var file = new[] { "a", "b", "c", "a", "b", "d" };

            // "a b" appears twice; the reported line decides which one the edit meant.
            Assert.Equal(3, DiffEngine.Locate(file, "a\nb", 4));
            Assert.Equal(0, DiffEngine.Locate(file, "a\nb", 1));
            Assert.Equal(2, DiffEngine.Locate(file, "c", 1));
            Assert.Equal(-1, DiffEngine.Locate(file, "gone", 1));
            Assert.Equal(-1, DiffEngine.Locate(file, string.Empty, 1));
        }

        [Fact]
        public void Compute_produces_unified_diff_matching_reference_image()
        {
            var before =
                "# Phase 2 gate closure plan\n\n**Date:** 2026-08-30\n**Status:** In progress\n\n## Objective\n\nClose the remaining Phase 2 implementation and verification gaps without";

            var after =
                "# Phase 2 gate closure plan\n\n**Date:** 2026-08-30\n**Status:** Gate closed 2026-08-30 except native hardware (harness/managed gates green, scanner Dotnet-aware)\n\n## Objective\n\nClose the remaining Phase 2 implementation and verification gaps without";

            var result = DiffEngine.Compute(before, after, startLine: 1);

            // Context lines before
            Assert.Equal(DiffKind.Unchanged, result[0].Kind);
            Assert.Equal("1", result[0].DisplayLineNumber);
            Assert.Equal("# Phase 2 gate closure plan", result[0].Text);

            Assert.Equal(DiffKind.Unchanged, result[1].Kind);
            Assert.Equal("2", result[1].DisplayLineNumber);

            Assert.Equal(DiffKind.Unchanged, result[2].Kind);
            Assert.Equal("3", result[2].DisplayLineNumber);
            Assert.Equal("**Date:** 2026-08-30", result[2].Text);

            // Line 4: Removed line
            Assert.Equal(DiffKind.Removed, result[3].Kind);
            Assert.Equal("4", result[3].DisplayLineNumber);
            Assert.Equal("**Status:** In progress", result[3].Text);

            // Line 4: Added line
            Assert.Equal(DiffKind.Added, result[4].Kind);
            Assert.Equal("4", result[4].DisplayLineNumber);
            Assert.Equal("**Status:** Gate closed 2026-08-30 except native hardware (harness/managed gates green, scanner Dotnet-aware)", result[4].Text);

            // Context lines after
            Assert.Equal(DiffKind.Unchanged, result[5].Kind);
            Assert.Equal("5", result[5].DisplayLineNumber);

            Assert.Equal(DiffKind.Unchanged, result[6].Kind);
            Assert.Equal("6", result[6].DisplayLineNumber);
            Assert.Equal("## Objective", result[6].Text);

            Assert.Equal(DiffKind.Unchanged, result[7].Kind);
            Assert.Equal("7", result[7].DisplayLineNumber);

            Assert.Equal(DiffKind.Unchanged, result[8].Kind);
            Assert.Equal("8", result[8].DisplayLineNumber);
            Assert.Equal("Close the remaining Phase 2 implementation and verification gaps without", result[8].Text);
        }
    }
}
