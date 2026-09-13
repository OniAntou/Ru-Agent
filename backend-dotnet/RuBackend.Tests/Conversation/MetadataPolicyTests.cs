using RuBackend.Conversation;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class MetadataPolicyTests
{
    private readonly MetadataPolicy _policy = new();

    [Fact]
    public void SplitReply_strips_private_markers_and_keeps_emotion()
    {
        var (visible, emotion) = _policy.SplitReply(
            "Hay quá! [[EMO:joy]] [[VTS:vts_01, expr_01, vts_02]]");

        Assert.Equal("Hay quá!", visible);
        Assert.Equal("joy", emotion);
        Assert.DoesNotContain("[[VTS:", visible, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Hay quá! [[VTS:vts_99]]")]
    [InlineData("Hay quá! [[VTS:vts_01,vts_01]]")]
    [InlineData("Hay quá! [[VTS:]]")]
    public void SplitReply_strips_every_marker_shape(string raw)
    {
        var (visible, _) = _policy.SplitReply(raw);

        Assert.Equal("Hay quá!", visible);
        Assert.DoesNotContain("[[VTS:", visible, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SplitReply_derives_emotion_from_text_when_the_marker_is_absent()
    {
        var (visible, emotion) = _policy.SplitReply(
            "[[VTS:vts_01]] Em buồn quá. [[EMO:not-private-data]]");

        Assert.Equal("Em buồn quá.", visible);
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
        var (visible, _) = _policy.SplitReply(
            "[[EMO:joy]] [[VTS:vts_01]] [[VOICE_PACE:quick]]");
        visible = _policy.StripVoiceMarkers(visible);

        Assert.Equal(MetadataPolicy.EmptyResponseText, visible);
    }
}
