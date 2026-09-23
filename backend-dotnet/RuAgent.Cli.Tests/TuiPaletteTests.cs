using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiPaletteTests
{
    [Fact]
    public void TrueColor_EmitsAuroraRgbCodes()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            Assert.Equal("\u001b[38;2;56;189;248m", TuiPalette.Code(TuiToken.Brand));
            Assert.Equal("\u001b[38;2;129;140;248m", TuiPalette.Code(TuiToken.Brand2));
            Assert.Equal("\u001b[38;2;148;163;184m", TuiPalette.Code(TuiToken.Meta));
            Assert.Equal("\u001b[38;2;52;211;153m", TuiPalette.Code(TuiToken.Ok));
            Assert.Equal("\u001b[38;2;251;191;36m", TuiPalette.Code(TuiToken.Warn));
            Assert.Equal("\u001b[38;2;248;113;113m", TuiPalette.Code(TuiToken.Err));
        }
    }

    [Fact]
    public void Palette256_EmitsIndexedCodes()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.Palette256))
        {
            Assert.Equal("\u001b[38;5;39m", TuiPalette.Code(TuiToken.Brand));
            Assert.Equal("\u001b[38;5;105m", TuiPalette.Code(TuiToken.Brand2));
            Assert.Equal("\u001b[38;5;246m", TuiPalette.Code(TuiToken.Meta));
            Assert.Equal("\u001b[38;5;79m", TuiPalette.Code(TuiToken.Ok));
            Assert.Equal("\u001b[38;5;221m", TuiPalette.Code(TuiToken.Warn));
            Assert.Equal("\u001b[38;5;210m", TuiPalette.Code(TuiToken.Err));
        }
    }

    [Fact]
    public void Basic16_EmitsBrightColourCodes()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.Basic16))
        {
            Assert.Equal("\u001b[94m", TuiPalette.Code(TuiToken.Brand));
            Assert.Equal("\u001b[96m", TuiPalette.Code(TuiToken.Brand2));
            Assert.Equal("\u001b[37m", TuiPalette.Code(TuiToken.Meta));
            Assert.Equal("\u001b[90m", TuiPalette.Code(TuiToken.Line));
            Assert.Equal("\u001b[92m", TuiPalette.Code(TuiToken.Ok));
            Assert.Equal("\u001b[93m", TuiPalette.Code(TuiToken.Warn));
            Assert.Equal("\u001b[91m", TuiPalette.Code(TuiToken.Err));
        }
    }

    [Fact]
    public void None_EmitsNoEscapeAtAll()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            Assert.Equal(string.Empty, TuiPalette.Code(TuiToken.Brand));
            Assert.Equal("hello", TuiPalette.Paint("hello", TuiToken.Brand));
            Assert.Equal("hello", TuiPalette.BoldPaint("hello"));
            Assert.Equal("hello", TuiPalette.DimPaint("hello"));
        }
    }

    [Fact]
    public void Ink_NeverEmitsForeground()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            Assert.Equal(string.Empty, TuiPalette.Code(TuiToken.Ink));
            Assert.Equal("prose", TuiPalette.Paint("prose", TuiToken.Ink));
        }
    }

    [Fact]
    public void Paint_WrapsAndResets()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            Assert.Equal("\u001b[38;2;56;189;248m◆\u001b[0m", TuiPalette.Paint("◆", TuiToken.Brand));
        }
    }

    [Fact]
    public void Line_token_is_deeper_than_meta_at_all_levels()
    {
        foreach (var level in new[] { TuiColorLevel.TrueColor, TuiColorLevel.Palette256, TuiColorLevel.Basic16 })
        {
            using (TuiPalette.PinLevel(level))
            {
                Assert.NotEqual(TuiPalette.Code(TuiToken.Line), TuiPalette.Code(TuiToken.Meta));
            }
        }
    }

    [Fact]
    public void Level_SetterOverridesDetection()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.Basic16))
        {
            Assert.Equal(TuiColorLevel.Basic16, TuiPalette.Level);
        }
    }

    [Fact]
    public void PinLevel_RestoresThePreviousValue()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            Assert.Equal(TuiColorLevel.TrueColor, TuiPalette.Level);
            using (TuiPalette.PinLevel(TuiColorLevel.None))
            {
                Assert.Equal(TuiColorLevel.None, TuiPalette.Level);
            }
            Assert.Equal(TuiColorLevel.TrueColor, TuiPalette.Level);
        }
    }

    [Theory]
    [InlineData("truecolor", TuiColorLevel.TrueColor)]
    [InlineData("24bit", TuiColorLevel.TrueColor)]
    [InlineData("256", TuiColorLevel.Palette256)]
    [InlineData("16", TuiColorLevel.Basic16)]
    [InlineData("none", TuiColorLevel.None)]
    [InlineData(" 256 ", TuiColorLevel.Palette256)]
    [InlineData("bogus", TuiColorLevel.Palette256)]
    public void DetectFrom_HonoursExplicitOverride(string requested, TuiColorLevel expected)
    {
        Assert.Equal(expected, TuiPalette.DetectFrom(requested, null, false, "xterm-256color", null, null, null));
    }

    [Fact]
    public void DetectFrom_DisablesColourForNoColorRedirectAndDumbTerminal()
    {
        Assert.Equal(TuiColorLevel.None, TuiPalette.DetectFrom(null, "1", false, "xterm-256color", null, null, null));
        Assert.Equal(TuiColorLevel.None, TuiPalette.DetectFrom(null, null, true, "xterm-256color", null, null, null));
        Assert.Equal(TuiColorLevel.None, TuiPalette.DetectFrom(null, null, false, "dumb", null, null, null));
    }

    [Fact]
    public void DetectFrom_PrefersTrueColorWhenTheTerminalAdvertisesIt()
    {
        Assert.Equal(TuiColorLevel.TrueColor, TuiPalette.DetectFrom(null, null, false, "xterm-256color", "truecolor", null, null));
        Assert.Equal(TuiColorLevel.TrueColor, TuiPalette.DetectFrom(null, null, false, "xterm-256color", null, "session", null));
        Assert.Equal(TuiColorLevel.TrueColor, TuiPalette.DetectFrom(null, null, false, "xterm-direct", null, null, null));
        Assert.Equal(TuiColorLevel.TrueColor, TuiPalette.DetectFrom(null, null, false, "xterm-256color", null, null, "WezTerm"));
    }

    [Fact]
    public void DetectFrom_DefaultsToPalette256()
    {
        Assert.Equal(TuiColorLevel.Palette256, TuiPalette.DetectFrom(null, null, false, "xterm-256color", null, null, null));
    }
}
