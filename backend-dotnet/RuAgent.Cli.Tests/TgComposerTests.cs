using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgComposerTests
{
    private static string[] Render(int width, int rows, Action<TgComposerView> configure)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = 5 };
        composer.SetMeta("Standard", "stealth/space-bunny-alpha", "kilo");
        composer.SetPlaceholder("Ask anything, / for commands, /key for sign-in…");
        window.Add(composer);
        configure(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rowsText = TgSnapshot.Capture(app);
            Assert.Equal(
                TgPalette.ToColor(TuiPalette.SurfaceRgb()),
                TgSnapshot.CellAttribute(app, rowsText.Length - 6, 40).Background);
            Assert.Contains("Standard", rowsText[rowsText.Length - 3]);
            Assert.Contains("kilo", rowsText[rowsText.Length - 3]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        return TgSnapshot.Capture(app);
    }

    [Fact]
    public void Idle_ShowsFilledPromptPlaceholderAndMeta()
    {
        var rows = Render(100, 30, _ => { });
        Assert.Contains("Ask anything", rows[rows.Length - 5]);
        Assert.DoesNotContain("╭", string.Join('\n', rows));
        Assert.DoesNotContain("models.dev", string.Join('\n', rows));
    }

    [Fact]
    public void Interior_UsesTheExistingRuPanelSurface()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = 5 };
        window.Add(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rowsText = TgSnapshot.Capture(app);
            var surface = TgPalette.ToColor(TuiPalette.SurfaceRgb());
            Assert.Equal(surface, TgSnapshot.CellAttribute(app, rowsText.Length - 5, 40).Background);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Composer_HasNoAccentRailAndKeepsThePanelSurface()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = 5 };
        window.Add(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rowsText = TgSnapshot.Capture(app);
            var surface = TgPalette.ToColor(TuiPalette.SurfaceRgb());
            var top = rowsText.Length - 6;

            // The composer carries no rail: the left column is blank surface on every row.
            Assert.Equal(" ", app.Driver!.Contents![top, 0].Grapheme);
            Assert.Equal(surface, TgSnapshot.CellAttribute(app, top, 0).Background);
            Assert.Equal(" ", app.Driver!.Contents![top + 4, 0].Grapheme);
            Assert.Equal(surface, TgSnapshot.CellAttribute(app, top + 4, 0).Background);
            Assert.Equal(surface, TgSnapshot.CellAttribute(app, top, 40).Background);
            Assert.Equal(surface, TgSnapshot.CellAttribute(app, top + 2, 40).Background);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
