using System.Xml.Linq;
using Xunit;

namespace RuApp.Tests;

public sealed class ActivityChevronHoverSourceContractTests
{
    /// <summary>
    /// The fold chevron stays out of the way until the reader is on the row.
    /// </summary>
    /// <remarks>
    /// Both toggles in the activity template are covered, because a rule that held for one of
    /// them would be a rule the feed does not actually follow.
    /// </remarks>
    [Fact]
    public void The_fold_chevron_is_hidden_until_the_row_is_hovered_or_focused()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");

        foreach (var name in new[] { "ThinkingToggle", "EditPreviewToggle" })
        {
            var toggle = GuiSource.Require(document, name);
            Assert.Equal("OnActivityTogglePointerEntered", GuiSource.AttributeValue(toggle, "PointerEntered"));
            Assert.Equal("OnActivityTogglePointerExited", GuiSource.AttributeValue(toggle, "PointerExited"));

            // Focus counts as hovering: a keyboard reader has no pointer to reveal it with.
            Assert.Equal("OnActivityToggleGotFocus", GuiSource.AttributeValue(toggle, "GotFocus"));
            Assert.Equal("OnActivityToggleLostFocus", GuiSource.AttributeValue(toggle, "LostFocus"));
        }

        var chevrons = document
            .Descendants()
            .Where(element => element.Name.LocalName == "FontIcon"
                && (GuiSource.AttributeValue(element, "Glyph") ?? string.Empty)
                    .Contains("ChevronGlyph", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, chevrons.Count);

        // Hidden by collapsing the glyph, never by dimming a muted brush further, and the box
        // it sits in keeps its width so the label beside it does not jump when it appears.
        Assert.All(chevrons, chevron => Assert.Equal("Collapsed", GuiSource.AttributeValue(chevron, "Visibility")));
        Assert.All(chevrons, chevron =>
        {
            Assert.Equal("Grid", chevron.Parent?.Name.LocalName);
            Assert.Equal("14", GuiSource.AttributeValue(chevron.Parent!, "Width"));
        });
    }

    [Fact]
    public void The_handler_collapses_and_reveals_the_chevron_rather_than_dimming_it()
    {
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "SetActivityChevronVisible");

        Assert.Contains("Visibility", body, StringComparison.Ordinal);
        Assert.Contains("FontIcon", body, StringComparison.Ordinal);
        Assert.Contains("ToggleButton", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Opacity", body, StringComparison.Ordinal);
    }
}
