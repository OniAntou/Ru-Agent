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
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(7), Width = Dim.Fill(), Height = 6 };
        composer.SetMeta("Standard", "stealth/space-bunny-alpha", "kilo");
        composer.SetPlaceholder("Ask anything, / for commands, /key for sign-in…");
        window.Add(composer);
        configure(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rowsText = TgSnapshot.Capture(app);
            Assert.Contains("┌", rowsText[rowsText.Length - 7]);
            Assert.Contains("Standard", rowsText[rowsText.Length - 3]);
            Assert.Contains("kilo", rowsText[rowsText.Length - 3]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        return TgSnapshot.Capture(app);
    }

    [Fact]
    public void Idle_ShowsSquareFramePlaceholderAndMeta()
    {
        var rows = Render(100, 30, _ => { });
        Assert.Contains("Ask anything", rows[rows.Length - 5]);
        Assert.DoesNotContain("╭", string.Join('\n', rows));
        Assert.DoesNotContain("models.dev", string.Join('\n', rows));
    }

    [Fact]
    public void Interior_HasNoFill_JustTheTerminalBackground()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(7), Width = Dim.Fill(), Height = 6 };
        window.Add(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rowsText = TgSnapshot.Capture(app);
            var black = new Terminal.Gui.Drawing.Color(0, 0, 0, 255);
            Assert.Equal(black, TgSnapshot.CellAttribute(app, rowsText.Length - 5, 40).Background);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Frame_IsGreenOnTheTerminalBackground()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(7), Width = Dim.Fill(), Height = 6 };
        window.Add(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rowsText = TgSnapshot.Capture(app);
            var black = new Terminal.Gui.Drawing.Color(0, 0, 0, 255);
            var green = TgPalette.ToColor(TuiToken.Ok);
            var top = rowsText.Length - 7;

            // Every border cell is green on the terminal default; the interior has no fill.
            Assert.Equal(green, TgSnapshot.CellAttribute(app, top, 0).Foreground);
            Assert.Equal(green, TgSnapshot.CellAttribute(app, top, 40).Foreground);
            Assert.Equal(green, TgSnapshot.CellAttribute(app, top + 5, 0).Foreground);
            Assert.Equal(black, TgSnapshot.CellAttribute(app, top, 40).Background);
            Assert.Equal(black, TgSnapshot.CellAttribute(app, top + 1, 1).Background);
            Assert.Equal(black, TgSnapshot.CellAttribute(app, top + 4, 40).Background);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
