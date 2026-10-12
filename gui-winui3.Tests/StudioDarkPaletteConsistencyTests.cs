using System.Xml.Linq;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class StudioDarkPaletteConsistencyTests
{
    [Fact]
    public void DefaultPacksKeepMeteorAndStarryIndependent()
    {
        var meteor = RuThemePalettes.PaletteFor(UiTheme.DarkMeteorShower);
        string Hex(Windows.UI.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        Assert.Equal(new[] { "#16181D", "#111317", "#20232A", "#F1F5F9", "#94A3B8", "#334155", "#282D37" },
            new[] { meteor.Canvas, meteor.Sidebar, meteor.Surface, meteor.Ink, meteor.Muted, meteor.Line, meteor.AccentSoft }.Select(Hex));
        Assert.Equal("#E2E8F0", Hex(meteor.Accent));
        Assert.Same(RuThemePalettes.StudioDark, RuThemePalettes.PaletteFor(UiTheme.Dark));
        Assert.Same(RuThemePalettes.StudioLight, RuThemePalettes.PaletteFor(UiTheme.Light));
        Assert.Same(RuThemePalettes.StarryNight, RuThemePalettes.PaletteFor(UiTheme.StarryNight));
        Assert.NotEqual(meteor.Canvas, RuThemePalettes.StudioDark.Canvas);
    }
    [Fact]
    public void StartupAndRuntimeDefaultPacksAgreeAndKeepNeutralInteractionSurfaces()
    {
        var key = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key";
        foreach (var (themeName, theme) in new[] { ("Light", UiTheme.Light), ("Dark", UiTheme.Dark) })
        {
            var themeDictionary = XDocument.Load(GuiSource.Path("App.xaml")).Descendants()
                .Single(element => element.Name.LocalName == "ResourceDictionary" && (string?)element.Attribute(key) == themeName);
            var startupValues = themeDictionary.Elements().ToDictionary(element => (string)element.Attribute(key)!,
                element => ((string?)element.Attribute("Color") ?? element.Value).Trim());
            var runtimePalette = RuThemePalettes.PaletteFor(theme);
            var colors = new Dictionary<string, Windows.UI.Color>
            {
                ["RuCanvasColor"] = runtimePalette.Canvas, ["RuSidebarColor"] = runtimePalette.Sidebar,
                ["RuSurfaceColor"] = runtimePalette.Surface, ["RuInkColor"] = runtimePalette.Ink,
                ["RuMutedColor"] = runtimePalette.Muted, ["RuLineColor"] = runtimePalette.Line,
                ["RuAccentColor"] = runtimePalette.Accent, ["RuAccentSoftColor"] = runtimePalette.AccentSoft,
                ["RuAccentHoverColor"] = runtimePalette.AccentHover, ["RuAccentPressedColor"] = runtimePalette.AccentPressed,
                ["RuSuccessColor"] = runtimePalette.Success, ["RuWarningColor"] = runtimePalette.Warning,
                ["RuDangerColor"] = runtimePalette.Danger, ["RuInfoColor"] = runtimePalette.Info,
                ["RuVioletColor"] = runtimePalette.Violet, ["RuPinkColor"] = runtimePalette.Pink,
            };
            foreach (var (name, color) in colors)
                Assert.Equal($"#{color.R:X2}{color.G:X2}{color.B:X2}", startupValues[name]);
        }
        var palette = RuThemePalettes.StudioDark;
        var dark = XDocument.Load(GuiSource.Path("App.xaml")).Descendants()
            .Single(element => element.Name.LocalName == "ResourceDictionary" && (string?)element.Attribute(key) == "Dark");
        var values = dark.Elements().ToDictionary(element => (string)element.Attribute(key)!,
            element => ((string?)element.Attribute("Color") ?? element.Value).Trim());
        foreach (var color in new[] { palette.Sidebar, palette.Canvas, palette.Surface, palette.AccentSoft, palette.Line })
        {
            Assert.Equal(color.R, color.G);
            Assert.Equal(color.G, color.B);
        }
        Assert.True(palette.Sidebar.R < palette.Canvas.R);
        Assert.True(palette.Canvas.R < palette.Surface.R);
        Assert.True(palette.Surface.R < palette.AccentSoft.R);
        foreach (var name in new[] { "ButtonBackgroundPointerOver", "ListViewItemBackgroundPointerOver", "ComboBoxItemBackgroundPointerOver" })
            Assert.Equal(values["RuSurfaceColor"], values[name]);
        Assert.Equal(values["RuAccentSoftColor"], values["ListViewItemBackgroundSelected"]);
    }
}
