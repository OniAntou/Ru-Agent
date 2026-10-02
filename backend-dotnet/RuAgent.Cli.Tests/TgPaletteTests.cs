using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Xunit;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace RuAgent.Cli.Tests;

public sealed class TgPaletteTests
{
    [Fact]
    public void Rgb_MatchesTheEmittedTrueColorCodes()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            Assert.Equal((92, 156, 245), TuiPalette.Rgb(TuiToken.Brand));
            Assert.Equal((238, 238, 238), TuiPalette.Rgb(TuiToken.Bright));
            Assert.Equal((51, 51, 51), TuiPalette.ElementRgb());
            Assert.Contains("38;2;92;156;245", TuiPalette.Code(TuiToken.Brand), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Scheme_PinsEveryVisualRoleSoTheFrameworkCannotSwapColours()
    {
        var scheme = TgPalette.Scheme(TuiToken.Ink, TuiPalette.ElementRgb());
        var normal = new Attr(TgPalette.ToColor(TuiToken.Ink), TgPalette.ToColor(TuiPalette.ElementRgb()));

        Assert.Equal(normal, scheme.Normal);
        Assert.Equal(normal, scheme.Focus);
        Assert.Equal(normal, scheme.Editable);
        Assert.Equal(normal, scheme.Active);
    }
}
