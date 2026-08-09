using System.Text.Json;
using RuBackend.Memory;
using Xunit;

namespace RuBackend.Tests.Memory;

public sealed class MemoryPolicyTests
{
    private readonly IMemoryPolicy _policy = new MemoryPolicy();

    [Fact]
    public void Extracts_vietnamese_preference_and_scoped_decision()
    {
        var candidates = _policy.ExtractCandidates(
            "Anh th\u00EDch Ru t\u1EF1 \u0111\u1ED9ng ho\u00E1 v\u00E0 d\u1EF1 \u00E1n D:\\LLM s\u1EBD d\u00F9ng SQLite cho memory.",
            "Em nh\u1EDB r\u1ED3i anh.");

        Assert.Collection(
            candidates,
            preference =>
            {
                Assert.Equal(MemoryKind.Preference, preference.Kind);
                Assert.Equal("Anh th\u00EDch Ru t\u1EF1 \u0111\u1ED9ng ho\u00E1.", preference.Content);
                Assert.Null(preference.Scope);
                Assert.Equal(1.0, preference.Confidence);
                Assert.Equal(0.8, preference.Importance);
            },
            decision =>
            {
                Assert.Equal(MemoryKind.Decision, decision.Kind);
                Assert.Equal("D:\\LLM s\u1EBD d\u00F9ng SQLite cho memory", decision.Content);
                Assert.Equal("D:\\LLM", decision.Scope);
                Assert.Equal(1.0, decision.Confidence);
                Assert.Equal(0.9, decision.Importance);
            });
    }

    [Fact]
    public void Extracts_english_preference_content_and_decision_keywords()
    {
        var candidates = _policy.ExtractCandidates(
            "Anh th\u00EDch concise replies v\u00E0 project D:\\LLM will use SQLite.",
            "Okay.");

        Assert.Collection(
            candidates,
            preference =>
            {
                Assert.Equal(MemoryKind.Preference, preference.Kind);
                Assert.Equal("Anh th\u00EDch concise replies.", preference.Content);
                Assert.Null(preference.Scope);
            },
            decision =>
            {
                Assert.Equal(MemoryKind.Decision, decision.Kind);
                Assert.Equal("D:\\LLM will use SQLite", decision.Content);
                Assert.Equal("D:\\LLM", decision.Scope);
            });
    }

    [Theory]
    [InlineData(@"D:\LLM\")]
    [InlineData(@"D:\LLM\.\")]
    [InlineData(@"D:\LLM\\")]
    [InlineData(@"d:\LLM\")]
    public void Canonicalizes_equivalent_windows_scope_forms(string path)
    {
        var candidates = _policy.ExtractCandidates(
            $"Anh th\u00EDch concise replies v\u00E0 project {path} s\u1EBD d\u00F9ng SQLite.",
            "Okay.");

        var decision = Assert.Single(candidates, candidate => candidate.Kind == MemoryKind.Decision);
        Assert.Equal(path[0] == 'd' ? @"d:\LLM" : @"D:\LLM", decision.Scope);
        Assert.StartsWith(path, decision.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("password l\u00E0 abc123")]
    [InlineData("Authorization: Bearer eyJabc.def.ghi")]
    [InlineData("https://example.com")]
    [InlineData("[[VTS:vts_01]]")]
    [InlineData("[[VOICE_PACE:1.10]]")]
    [InlineData("[[EMO:happy]]")]
    public void Rejects_blank_sensitive_and_private_input(string text)
    {
        Assert.True(_policy.IsSensitiveOrUnsafe(text));
        Assert.Empty(_policy.ExtractCandidates(text, "Em \u0111\u00E3 nh\u1EADn."));
    }

    [Fact]
    public void Strips_vts_metadata_without_leaking_it_to_candidates()
    {
        Assert.Equal("Hello  world", _policy.StripInternalMetadata("Hello [[VTS:vts_01]] world"));
        var candidates = _policy.ExtractCandidates("Anh th\u00EDch pizza [[VTS:vts_01]]", "Em bi\u1EBFt r\u1ED3i.");
        Assert.Single(candidates);
        Assert.DoesNotContain("VTS", candidates[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VTS", candidates[0].Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalizes_unicode_whitespace_and_bounds_candidate_fields()
    {
        var decomposed = "Anh thi\u0301ch pizza\r\n";
        var candidates = _policy.ExtractCandidates(decomposed, "ok");

        Assert.Single(candidates);
        Assert.Equal("Anh th\u00EDch pizza.", candidates[0].Content);
        Assert.DoesNotContain("\r", candidates[0].Evidence);
        Assert.DoesNotContain("\n", candidates[0].Evidence);
        Assert.True(candidates[0].Evidence.Length <= 2_000);
        Assert.True(candidates[0].Content.Length <= 100_000);
    }

    [Fact]
    public void Rejects_possessive_address_and_deduplicates_normalized_preference_matches()
    {
        Assert.Empty(_policy.ExtractCandidates("Em trai anh th\u00EDch b\u00F3ng \u0111\u00E1.", "ok"));

        var candidates = _policy.ExtractCandidates(
            "Anh thi\u0301ch pizza. Anh th\u00EDch pizza. Anh th\u00EDch tea.",
            "ok");

        var preferences = candidates.Where(candidate => candidate.Kind == MemoryKind.Preference).ToArray();
        Assert.Equal(2, preferences.Length);
        Assert.Equal("Anh th\u00EDch pizza.", preferences[0].Content);
        Assert.Equal("Anh th\u00EDch tea.", preferences[1].Content);
    }

    [Fact]
    public void Policy_fixture_cases_are_loaded_and_asserted()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindFixturePath()));
        var cases = document.RootElement.GetProperty("policyCases").EnumerateArray().ToArray();

        Assert.Equal(4, cases.Length);
        foreach (var policyCase in cases)
        {
            var candidates = _policy.ExtractCandidates(
                policyCase.GetProperty("prompt").GetString() ?? string.Empty,
                policyCase.GetProperty("response").GetString() ?? string.Empty);
            var expectedKind = policyCase.GetProperty("kind");

            if (expectedKind.ValueKind == JsonValueKind.Null)
            {
                Assert.Empty(candidates);
                continue;
            }

            var kind = Enum.Parse<MemoryKind>(expectedKind.GetString()!, ignoreCase: true);
            var candidate = Assert.Single(candidates, candidate => candidate.Kind == kind);
            Assert.Equal(policyCase.GetProperty("content").GetString(), candidate.Content);
            var expectedScope = policyCase.GetProperty("scope");
            Assert.Equal(
                expectedScope.ValueKind == JsonValueKind.Null ? null : expectedScope.GetString(),
                candidate.Scope);
        }
    }

    [Fact]
    public void Clamps_candidate_values_and_limits_evidence()
    {
        var prompt = "Anh th\u00EDch pizza. " + new string('x', 2_500);
        var candidate = Assert.Single(_policy.ExtractCandidates(prompt, "ok"));

        Assert.InRange(candidate.Confidence, 0, 1);
        Assert.InRange(candidate.Importance, 0, 1);
        Assert.True(candidate.Evidence.Length <= 2_000);
        Assert.True(candidate.Content.Length <= 100_000);
    }

    private static string FindFixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures", "memory-parity.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate tests/fixtures/memory-parity.json.");
    }
}
