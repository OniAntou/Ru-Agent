using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// A long prompt in the composer wraps to the box's width, grows with the window and
/// scrolls vertically once it reaches its cap.
/// </summary>
public sealed class ComposerInputLayoutTests
{
    [Fact]
    public void The_composer_wraps_long_lines_and_scrolls_only_vertically()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var composer = GuiSource.Require(document, "ChatInputTextBox");

        // Without wrapping a long paragraph ran off the right edge as one line.
        Assert.Equal("Wrap", GuiSource.AttributeValue(composer, "TextWrapping"));
        Assert.Equal("Auto", GuiSource.AttributeValue(composer, "ScrollViewer.VerticalScrollBarVisibility"));
        Assert.Equal("Disabled", GuiSource.AttributeValue(composer, "ScrollViewer.HorizontalScrollBarVisibility"));
        Assert.Equal("True", GuiSource.AttributeValue(composer, "AcceptsReturn"));
    }

    [Fact]
    public void The_cap_follows_the_view_height_between_its_bounds()
    {
        Assert.Equal(ComposerInputHeightPolicy.MinimumMaxHeight, ComposerInputHeightPolicy.MaxHeightFor(0));
        Assert.Equal(ComposerInputHeightPolicy.MinimumMaxHeight, ComposerInputHeightPolicy.MaxHeightFor(double.NaN));
        Assert.Equal(ComposerInputHeightPolicy.MinimumMaxHeight, ComposerInputHeightPolicy.MaxHeightFor(400));
        Assert.Equal(280, ComposerInputHeightPolicy.MaxHeightFor(800));
        Assert.Equal(ComposerInputHeightPolicy.MaximumMaxHeight, ComposerInputHeightPolicy.MaxHeightFor(2160));
    }

    [Fact]
    public void Resizing_the_view_reapplies_the_cap()
    {
        string code = GuiSource.ViewText("ChatView");
        string body = GuiSource.MethodBody(code, "ApplyInputWidth");

        Assert.Contains("ChatInputTextBox.MaxHeight = ComposerInputHeightPolicy.MaxHeightFor(RootGrid.ActualHeight)", body, System.StringComparison.Ordinal);
    }
}
