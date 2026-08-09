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
            "Anh thích Ru tự động hoá và dự án D:\\LLM sẽ dùng SQLite cho memory.",
            "Em nhớ rồi anh.");

        Assert.Collection(
            candidates,
            preference =>
            {
                Assert.Equal(MemoryKind.Preference, preference.Kind);
                Assert.Equal("Anh thích Ru tự động hoá.", preference.Content);
                Assert.Null(preference.Scope);
                Assert.Equal(1.0, preference.Confidence);
                Assert.Equal(0.8, preference.Importance);
            },
            decision =>
            {
                Assert.Equal(MemoryKind.Decision, decision.Kind);
                Assert.Equal("D:\\LLM sẽ dùng SQLite cho memory", decision.Content);
                Assert.Equal("D:\\LLM", decision.Scope);
                Assert.Equal(1.0, decision.Confidence);
                Assert.Equal(0.9, decision.Importance);
            });
    }

    [Fact]
    public void Extracts_english_preference_and_decision_keywords()
    {
        var candidates = _policy.ExtractCandidates(
            "Anh prefers concise replies and project D:\\LLM will use SQLite.",
            "Okay.");

        Assert.Single(candidates);
        Assert.Equal(MemoryKind.Decision, candidates[0].Kind);
        Assert.Equal("D:\\LLM will use SQLite", candidates[0].Content);
        Assert.Equal("D:\\LLM", candidates[0].Scope);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("password là abc123")]
    [InlineData("Authorization: Bearer eyJabc.def.ghi")]
    [InlineData("https://example.com")]
    [InlineData("[[VTS:vts_01]]")]
    [InlineData("[[VOICE_PACE:1.10]]")]
    [InlineData("[[EMO:happy]]")]
    public void Rejects_blank_sensitive_and_private_input(string text)
    {
        Assert.True(_policy.IsSensitiveOrUnsafe(text));
        Assert.Empty(_policy.ExtractCandidates(text, "Em đã nhận."));
    }

    [Fact]
    public void Strips_vts_metadata_without_leaking_it_to_candidates()
    {
        Assert.Equal("Hello  world", _policy.StripInternalMetadata("Hello [[VTS:vts_01]] world"));
        var candidates = _policy.ExtractCandidates("Anh thích pizza [[VTS:vts_01]]", "Em biết rồi.");
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
        Assert.Equal("Anh thích pizza.", candidates[0].Content);
        Assert.DoesNotContain("\r", candidates[0].Evidence);
        Assert.DoesNotContain("\n", candidates[0].Evidence);
        Assert.True(candidates[0].Evidence.Length <= 2_000);
        Assert.True(candidates[0].Content.Length <= 100_000);
    }

    [Fact]
    public void Rejects_possessive_address_and_deduplicates_normalized_candidates()
    {
        Assert.Empty(_policy.ExtractCandidates("Em trai anh thích bóng đá.", "ok"));

        var candidates = _policy.ExtractCandidates("Anh thích pizza. Anh thích pizza.", "ok");
        Assert.Single(candidates);
        Assert.Equal("Anh thích pizza.", candidates[0].Content);
    }

    [Fact]
    public void Clamps_candidate_values_and_limits_evidence()
    {
        var prompt = "Anh thích pizza. " + new string('x', 2_500);
        var candidate = Assert.Single(_policy.ExtractCandidates(prompt, "ok"));

        Assert.InRange(candidate.Confidence, 0, 1);
        Assert.InRange(candidate.Importance, 0, 1);
        Assert.True(candidate.Evidence.Length <= 2_000);
        Assert.True(candidate.Content.Length <= 100_000);
    }
}
