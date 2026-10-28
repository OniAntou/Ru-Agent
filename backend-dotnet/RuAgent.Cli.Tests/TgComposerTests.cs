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
        var layoutDeadline = DateTime.UtcNow.AddSeconds(5);
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            var frame = window.Composer.Frame;
            var contents = app.Driver!.Contents!;
            // The ANSI driver can resize between iterations. Wait for layout
            // to match its buffer instead of sampling stale coordinates.
            if (frame.Top < 0 || frame.Left < 0
                || frame.Bottom > contents.GetLength(0) || frame.Right > contents.GetLength(1))
            {
                Assert.True(DateTime.UtcNow < layoutDeadline, "composer layout did not match the driver buffer");
                return true;
            }
            Assert.True(frame.Height > 0 && frame.Width > 0, "composer frame was empty");
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
            Assert.DoesNotContain("Standard", rowsText[composer.Frame.Y + TgComposerView.MetadataRow]);
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
    public void Preset_moves_to_header_next_to_usage_and_keeps_its_green_accent()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Transcript.Append("active conversation", TranscriptStyle.Prose);
        window.Composer.SetMeta("standard", "model", "provider");
        window.Header.UpdateUsage(39413, 20, 0.29m);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = TgSnapshot.Capture(app);
            var column = rows[0].IndexOf("standard", StringComparison.Ordinal);
            Assert.True(column >= 0); Assert.True(rows[0].IndexOf("39,413", StringComparison.Ordinal) > column);
            Assert.Equal("standard", window.Header.Preset);
            Assert.Equal(TgPalette.ToColor(TuiToken.Ok), TgSnapshot.CellAttribute(app, 0, column).Foreground);
            Assert.DoesNotContain("standard", rows[window.Composer.Frame.Y + TgComposerView.MetadataRow]);
            app.RequestStop(); return false;
        });
        app.Run(window);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("standard")]
    [InlineData("full_access")]
    public void Preset_remains_visible_in_a_narrow_header_and_updates_after_selection(string preset)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var header = new TgHeaderView { Width = 32, Height = 2 };
        header.SetTitle(new string('x', 100)); header.SetPreset("full_access"); header.UpdateUsage(39413, 20, 0.29m);
        window.Add(header); var step = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            if (step++ == 0) { header.SetPreset(preset); return true; }
            var row = TgSnapshot.Capture(app)[0];
            Assert.Contains(preset, row); Assert.Contains("39.4K", row);
            Assert.DoesNotContain("20.00%", row); Assert.DoesNotContain("$0.29", row);
            if (preset != "full_access") Assert.DoesNotContain("full_access", row);
            app.RequestStop(); return false;
        });
        app.Run(window);
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
