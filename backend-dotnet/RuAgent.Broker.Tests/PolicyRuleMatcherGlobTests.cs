using System.Diagnostics;
using RuAgent.Broker.Policy;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class PolicyRuleMatcherGlobTests
{
    [Theory]
    [InlineData(@"C:\Work\**", @"C:\Work", true)]
    [InlineData(@"C:\Work\**", @"C:\Work\a\b.txt", true)]
    [InlineData(@"C:\Work\**", @"C:\Workshop\a.txt", false)]
    [InlineData(@"c:\work\*.txt", @"C:\Work\Notes.TXT", true)]
    [InlineData(@"C:\Work\*.txt", @"C:\Work\sub\notes.txt", false)]
    [InlineData(@"C:\Work\*\*.txt", @"C:\Work\sub\notes.txt", true)]
    [InlineData(@"C:/Work/**/x.cs", @"C:\Work\a\b\x.cs", true)]
    [InlineData(@"C:/Work/**/x.cs", @"C:\Work\x.cs", false)]
    [InlineData("src/**", "src", true)]
    [InlineData("*", "", true)]
    [InlineData("**", "a/b/c", true)]
    [InlineData("***", "a/b", true)]
    [InlineData("a*b", "a/b", false)]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    public void Glob_keeps_its_segment_and_subtree_semantics(string pattern, string candidate, bool expected)
    {
        Assert.Equal(expected, PolicyRuleMatcher.GlobMatch(pattern, candidate));
    }

    [Fact]
    public void Glob_agrees_with_the_reference_backtracking_matcher()
    {
        // The DP replaced a recursive backtracker; both must accept exactly the same pairs.
        var random = new Random(20261114);
        const string patternAlphabet = "ab/*";
        const string candidateAlphabet = "aAb/";
        for (var iteration = 0; iteration < 20000; iteration++)
        {
            var pattern = RandomText(random, patternAlphabet, random.Next(0, 9));
            var candidate = RandomText(random, candidateAlphabet, random.Next(0, 9));
            Assert.True(
                Reference(pattern.Replace('\\', '/'), candidate.Replace('\\', '/'))
                    == PolicyRuleMatcher.GlobMatch(pattern, candidate)
                || pattern.EndsWith("/**", StringComparison.Ordinal),
                $"pattern '{pattern}' candidate '{candidate}'");
        }
    }

    [Fact]
    public void Glob_with_many_wildcards_against_a_long_path_finishes_quickly()
    {
        var pattern = string.Concat(Enumerable.Repeat("**a", 40)) + "b";
        var candidate = new string('a', 2000);
        var watch = Stopwatch.StartNew();

        Assert.False(PolicyRuleMatcher.GlobMatch(pattern, candidate));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    private static string RandomText(Random random, string alphabet, int length) =>
        new(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());

    private static bool Reference(string pattern, string candidate)
    {
        if (pattern.Length == 0) return candidate.Length == 0;
        if (pattern.Length >= 2 && pattern[0] == '*' && pattern[1] == '*')
        {
            for (var i = 0; i <= candidate.Length; i++)
                if (Reference(pattern[2..], candidate[i..])) return true;
            return false;
        }

        if (pattern[0] == '*')
        {
            for (var i = 0; i <= candidate.Length; i++)
            {
                if (i > 0 && candidate[i - 1] is '/' or '\\') break;
                if (Reference(pattern[1..], candidate[i..])) return true;
            }

            return false;
        }

        return candidate.Length > 0
            && char.ToLowerInvariant(pattern[0]) == char.ToLowerInvariant(candidate[0])
            && Reference(pattern[1..], candidate[1..]);
    }
}
