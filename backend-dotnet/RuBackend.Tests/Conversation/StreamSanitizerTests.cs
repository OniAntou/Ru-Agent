using System.Text;
using RuBackend.Conversation;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class StreamSanitizerTests
{
    private readonly StreamSanitizer _sanitizer = new();

    [Fact]
    public void Append_accepts_incremental_overlapping_and_cumulative_provider_text()
    {
        var raw = _sanitizer.Append(string.Empty, "Chào ");
        raw = _sanitizer.Append(raw, "o anh");
        Assert.Equal("Chào anh", raw);

        raw = _sanitizer.Append(raw, "Chào anh nhé");
        Assert.Equal("Chào anh nhé", raw);
        Assert.Equal(raw, _sanitizer.Append(raw, "Chào"));
    }

    [Theory]
    [InlineData("Chào anh [[VT", "Chào anh ")]
    [InlineData("Chào anh [[VTS:vts_01]]", "Chào anh ")]
    [InlineData("Chào anh [[VOICE_PA", "Chào anh ")]
    [InlineData("Chào anh [[VOICE_PACE:quick]]", "Chào anh ")]
    [InlineData("Chào anh [[EM", "Chào anh ")]
    [InlineData("Chào anh [[EMO:joy]]", "Chào anh ")]
    [InlineData("Để em tìm [SEAR", "Để em tìm ")]
    public void Visible_holds_partial_and_complete_private_markers(string raw, string expected)
    {
        Assert.Equal(expected, _sanitizer.Visible(raw));
    }

    [Fact]
    public void Marker_split_across_deltas_is_never_publicly_visible()
    {
        var raw = _sanitizer.Append(string.Empty, "Em nghe đây. [[VOI");
        var first = _sanitizer.Visible(raw);
        raw = _sanitizer.Append(raw, "CE_PACE:quick]] [[VTS:vts_01]]");
        var second = _sanitizer.Visible(raw);

        Assert.Equal("Em nghe đây. ", first);
        Assert.Equal("Em nghe đây. ", second);
        Assert.DoesNotContain("[[", first, StringComparison.Ordinal);
        Assert.DoesNotContain("[[", second, StringComparison.Ordinal);
    }

    [Fact]
    public void Visible_normalizes_to_NFC_without_altering_normal_brackets()
    {
        var decomposed = "Cha\u0300o anh dùng [value]";

        var visible = _sanitizer.Visible(decomposed);

        Assert.Equal("Chào anh dùng [value]", visible);
        Assert.True(visible.IsNormalized(NormalizationForm.FormC));
    }
}
