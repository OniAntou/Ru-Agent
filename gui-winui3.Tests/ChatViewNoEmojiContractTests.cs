using System.Text;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The chat surface ships no emoji. The invariant is about what the user sees, so
/// it is asserted against the markup that renders and the presentation strings, not
/// against every byte of the code-behind (comments and diagnostics may contain any
/// character).
/// </summary>
public sealed class ChatViewNoEmojiContractTests
{
    [Fact]
    public void ChatViewMarkupContainsNoEmojiInAnyRenderedText()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var offenders = new List<string>();

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (!EmojiPolicy.IsRenderedTextAttribute(attribute.Name.LocalName))
                {
                    continue;
                }

                foreach (var rune in EmojiPolicy.FindIn(attribute.Value))
                {
                    offenders.Add($"<{element.Name.LocalName} {attribute.Name.LocalName}> U+{rune.Value:X4}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void ChatViewCodeBehindDeclaresNoEmojiInStringLiterals()
    {
        var offenders = new List<string>();
        foreach (var literal in EmojiPolicy.StringLiterals(GuiSource.Text("Views", "ChatView.xaml.cs")))
        {
            foreach (var rune in EmojiPolicy.FindIn(literal))
            {
                offenders.Add($"U+{rune.Value:X4} in \"{Truncate(literal)}\"");
            }
        }

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(0x00A9)]
    [InlineData(0x203C)]
    [InlineData(0x2194)]
    [InlineData(0x231A)]
    [InlineData(0x2600)]
    [InlineData(0x2B50)]
    [InlineData(0x1F642)]
    [InlineData(0x1FAE0)]
    [InlineData(0xFE0F)]
    [InlineData(0x20E3)]
    [InlineData(0xE007F)]
    public void EmojiPolicyCoversEmojiPresentationAndCompositionRunes(int scalar)
    {
        Assert.True(EmojiPolicy.IsEmojiRune(new Rune(scalar)));
    }

    [Theory]
    [InlineData('A')]
    [InlineData('é')]
    [InlineData('—')]
    [InlineData('…')]
    [InlineData('·')]
    public void EmojiPolicyLeavesOrdinaryTypographyAlone(int scalar)
    {
        Assert.False(EmojiPolicy.IsEmojiRune(new Rune(scalar)));
    }

    [Theory]
    [InlineData(0x2713)]
    [InlineData(0x2717)]
    public void EmojiPolicyAlsoRejectsDingbatsTheProductDoesNotUse(int scalar)
    {
        // The policy covers the dingbat block even though a check mark can read as
        // ordinary typography, so the chat surface cannot grow one by accident.
        Assert.True(EmojiPolicy.IsEmojiRune(new Rune(scalar)));
    }

    private static string Truncate(string value) =>
        value.Length <= 48 ? value : value[..48] + "…";
}
