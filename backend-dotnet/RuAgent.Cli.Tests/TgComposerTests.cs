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
    private static Color Surface => TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()).Background;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposerIsOneFilledSurfaceWhenEmptyAndWhenEditing(bool typed)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Composer.SetMeta("Standard", "model", "provider");
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            window.FocusComposer();
            if (typed) window.Composer.Input.Text = "first line\nsecond line";
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            var frame = window.Composer.Frame;
            for (var row = frame.Top; row < frame.Bottom; row++)
            {
                for (var column = frame.Left; column < frame.Right; column++)
                {
                    Assert.Equal(Surface, TgSnapshot.CellAttribute(app, row, column).Background);
                }
            }

            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    private static string[] Render(int width, int rows, Action<TgComposerView> configure)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = TgComposerView.RowCount };
        composer.SetMeta("Standard", "stealth/space-bunny-alpha", "kilo");
        composer.SetPlaceholder("Ask anything, / for commands, /key for sign-in…");
        window.Add(composer);
        configure(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rowsText = TgSnapshot.Capture(app);
            Assert.Equal(Surface, TgSnapshot.CellAttribute(app, rowsText.Length - 6, 40).Background);
            Assert.Contains("Standard", rowsText[composer.Frame.Y + TgComposerView.MetadataRow]);
            Assert.Contains("kilo", rowsText[composer.Frame.Y + TgComposerView.MetadataRow]);
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
        var text = string.Join('\n', rows);
        Assert.Contains("Ask anything", rows[rows.Length - 5]);
        // The block is the shape: no frame glyphs and no accent rail.
        Assert.DoesNotContain("┌", text, StringComparison.Ordinal);
        Assert.DoesNotContain("└", text, StringComparison.Ordinal);
        Assert.DoesNotContain("┃", text, StringComparison.Ordinal);
        Assert.DoesNotContain("models.dev", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Interior_UsesTheExistingRuPanelSurface()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = TgComposerView.RowCount };
        window.Add(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rowsText = TgSnapshot.Capture(app);
            Assert.Equal(Surface, TgSnapshot.CellAttribute(app, rowsText.Length - 5, 40).Background);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Composer_HasNoFrameRailOrOutline()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = TgComposerView.RowCount };
        composer.SetMeta("Standard", "model", "provider");
        window.Add(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rowsText = TgSnapshot.Capture(app);
            var top = rowsText.Length - 6;
            var text = string.Join('\n', rowsText);

            // No border and no rail: the left edge is the fill itself.
            Assert.DoesNotContain("┌", text, StringComparison.Ordinal);
            Assert.DoesNotContain("─", text, StringComparison.Ordinal);
            Assert.DoesNotContain("│", text, StringComparison.Ordinal);
            Assert.NotEqual("│", app.Driver!.Contents![top + 1, 0].Grapheme);
            Assert.Equal(Surface, TgSnapshot.CellAttribute(app, top, 0).Background);
            Assert.Equal(Surface, TgSnapshot.CellAttribute(app, top + 4, 0).Background);
            Assert.Equal(Surface, TgSnapshot.CellAttribute(app, top + 2, 40).Background);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
