using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The composer's fixed vocabulary. The markup and the code-behind both read these,
/// so a change here is the only way either face can change.
/// </summary>
public sealed class AgentComposerPresentationTests
{
    [Fact]
    public void The_two_send_faces_never_share_a_glyph()
    {
        // A send arrow that still points the same way while the button means Stop is
        // how the control stops reading as its action.
        Assert.NotEqual(AgentComposerPresentation.SendGlyph, AgentComposerPresentation.StopGlyph);
    }

    [Fact]
    public void The_two_send_faces_never_share_an_announced_name()
    {
        // A screen reader must be able to tell "send" from "stop"; the same name on
        // both faces makes the control unusable without sight.
        Assert.NotEqual(AgentComposerPresentation.SendName, AgentComposerPresentation.StopName);
    }

    [Theory]
    [InlineData(true, "Stop Coding Agent")]
    [InlineData(false, "Send message")]
    public void The_announced_name_follows_the_face(bool isStopFace, string expected)
    {
        Assert.Equal(expected, AgentComposerPresentation.NameFor(isStopFace));
    }

    [Fact]
    public void The_glyph_follows_the_face()
    {
        Assert.Equal(AgentComposerPresentation.StopGlyph, AgentComposerPresentation.GlyphFor(isStopFace: true));
        Assert.Equal(AgentComposerPresentation.SendGlyph, AgentComposerPresentation.GlyphFor(isStopFace: false));
    }

    [Fact]
    public void Every_announced_name_is_usable_as_an_accessible_name()
    {
        // An empty or whitespace name is announced as nothing.
        foreach (var name in new[]
                 {
                     AgentComposerPresentation.SendName,
                     AgentComposerPresentation.StopName,
                     AgentComposerPresentation.AgentModeName,
                     AgentComposerPresentation.AgentModeLabelText,
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(name, name.Trim());
        }
    }

    [Fact]
    public void Every_glyph_is_a_single_segoe_asset_codepoint()
    {
        // The Segoe Fluent glyphs are one character in the private use area; a longer
        // string renders as text instead of an icon.
        foreach (var glyph in new[]
                 {
                     AgentComposerPresentation.SendGlyph,
                     AgentComposerPresentation.StopGlyph,
                     AgentComposerPresentation.AgentModeGlyph,
                 })
        {
            Assert.Single(glyph);
            Assert.InRange(glyph[0], '\uE000', '\uF8FF');
        }
    }
}
