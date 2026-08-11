using RuBackend.Conversation;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class MetadataPolicyTests
{
    private readonly MetadataPolicy _policy = new();

    [Fact]
    public void SplitReply_strips_private_markers_and_keeps_distinct_catalog_codes()
    {
        var catalog = Catalog(["vts_01", "vts_02"], ["expr_01"]);

        var (visible, codes, emotion) = _policy.SplitReply(
            "Hay quá! [[EMO:joy]] [[VTS:vts_01, expr_01, vts_02]]",
            catalog);

        Assert.Equal("Hay quá!", visible);
        Assert.Equal(["vts_01", "expr_01", "vts_02"], codes);
        Assert.Equal("joy", emotion);
    }

    [Theory]
    [InlineData("Hay quá! [[VTS:vts_99]]")]
    [InlineData("Hay quá! [[VTS:vts_01,vts_01]]")]
    [InlineData("Hay quá! [[VTS:]]")]
    public void SplitReply_strips_invalid_catalog_markers_without_triggering_actions(string raw)
    {
        var (visible, codes, _) = _policy.SplitReply(raw, Catalog(["vts_01"], []));

        Assert.Equal("Hay quá!", visible);
        Assert.Empty(codes);
        Assert.DoesNotContain("[[VTS:", visible, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SplitReply_strips_every_marker_even_when_no_catalog_is_available()
    {
        var (visible, codes, emotion) = _policy.SplitReply(
            "[[VTS:vts_01]] Em buồn quá. [[EMO:not-private-data]]",
            null);

        Assert.Equal("Em buồn quá.", visible);
        Assert.Empty(codes);
        Assert.Equal("sad", emotion);
    }

    [Fact]
    public void StripVoiceMarkers_removes_vts_voice_and_emotion_metadata_anywhere()
    {
        var visible = _policy.StripVoiceMarkers(
            "A [[VTS:vts_01]] B [[VOICE_PACE:quick]] [[EMO:joy]] C");

        Assert.Equal("A B C", visible);
    }

    [Fact]
    public void Marker_only_reply_becomes_the_controlled_safe_text()
    {
        var (visible, codes, _) = _policy.SplitReply(
            "[[EMO:joy]] [[VTS:vts_01]] [[VOICE_PACE:quick]]",
            Catalog(["vts_01"], []));
        visible = _policy.StripVoiceMarkers(visible);

        Assert.Equal(MetadataPolicy.EmptyResponseText, visible);
        Assert.Equal(["vts_01"], codes);
    }

    private static VtsCatalog Catalog(
        string[] hotkeys,
        string[] expressions) => new(
            new HashSet<string>(hotkeys, StringComparer.Ordinal),
            new HashSet<string>(expressions, StringComparer.Ordinal));
}
