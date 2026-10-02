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
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(7), Width = Dim.Fill(), Height = 7 };
        composer.SetMeta("Standard", "stealth/space-bunny-alpha", "kilo");
        composer.SetPlaceholder("Ask anything, / for commands, /key for sign-in…");
        window.Add(composer);
        configure(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rowsText = TgSnapshot.Capture(app);
            Assert.Contains("┌", rowsText[rowsText.Length - 7]);
            Assert.Contains("Standard", rowsText[rowsText.Length - 2]);
            Assert.Contains("kilo", rowsText[rowsText.Length - 2]);
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
        Assert.Contains("Ask anything", rows[rows.Length - 4]);
        Assert.DoesNotContain("╭", string.Join('\n', rows));
        Assert.DoesNotContain("models.dev", string.Join('\n', rows));
    }

    [Fact]
    public void Interior_IsTheGrayElementBand()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(7), Width = Dim.Fill(), Height = 7 };
        window.Add(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rowsText = TgSnapshot.Capture(app);
            var element = TgPalette.ToColor(TuiPalette.ElementRgb());
            Assert.Equal(element, TgSnapshot.CellAttribute(app, rowsText.Length - 5, 40).Background);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
