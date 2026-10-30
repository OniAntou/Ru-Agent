using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgOpenCodeLayoutTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    [InlineData(160, 50)]
    public void Welcome_keeps_a_large_galaxy_and_brings_input_close_to_the_logo(int width, int height)
    {
        Render(width, height, false, _ => { }, (_, w, screen) =>
        {
            Assert.InRange((double)w.Landing.SkyRows!.Value / height, 0.70, 0.76);
            Assert.Equal(w.Landing.WordmarkRow + TuiHeader.WordmarkRows + 2, w.Composer.Frame.Y);
            Assert.True(w.Composer.Frame.Y < height * 0.75);
            Assert.True(w.Hints.Frame.Bottom <= height);
            Assert.True(w.Landing.WordmarkRow + TuiHeader.WordmarkRows < w.Composer.Frame.Y);
            Assert.Contains("Ask anything", screen[w.Composer.Frame.Y + 1]);
            Assert.True(w.Header.Visible); Assert.False(w.Header.ShowSessionDetails);
        });
    }

    [Fact]
    public void Lower_welcome_stars_fill_empty_space_without_covering_input_or_hint()
    {
        Render(160, 50, false, _ => { }, (_, w, screen) =>
        {
            var lower = screen.Skip(w.Landing.SkyRows!.Value);
            Assert.Contains(string.Join('\n', lower), ch => ch is >= '\u2801' and <= '\u28ff');
            for (var row = w.Composer.Frame.Y; row < w.Composer.Frame.Bottom; row++)
            {
                var input = screen[row].PadRight(160).Substring(w.Composer.Frame.X, w.Composer.Frame.Width);
                Assert.DoesNotContain(input, ch => ch is >= '\u2801' and <= '\u28ff');
            }
            Assert.EndsWith("ctrl+p commands", screen[w.Hints.Frame.Y]);
        });
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    [InlineData(160, 50)]
    public void Welcome_logo_is_centered_on_the_galaxy_core(int columns, int rows)
    {
        Render(columns, rows, false, _ => { }, (app, w, screen) =>
        {
            var top = w.Landing.WordmarkRow!.Value;
            var coreRow = w.Landing.SkyRows!.Value * TuiGalaxySky.CoreYFraction;
            Assert.InRange(Math.Abs(top + TuiHeader.WordmarkRows / 2d - coreRow), 0, .5);
            Assert.Contains("█▀▀█ █  █ █▀▀█", screen[top]);
            Assert.True(top + TuiHeader.WordmarkRows < w.Composer.Frame.Y);
            var left = (columns - TgWelcomeWordmark.Width) / 2;
            Assert.Equal(TgPalette.ToColor((112, 225, 187)), TgSnapshot.CellAttribute(app, top + 1, left).Foreground);
            Assert.Equal("▀", app.Driver!.Contents![top + 3, left + 1].Grapheme);
            Assert.Equal(TgPalette.ToColor((13, 34, 38)), TgSnapshot.CellAttribute(app, top + 3, left + 1).Foreground);
        });
    }

    [Fact]
    public void Short_welcome_keeps_the_logo_and_thin_shadow_above_input()
    {
        Render(35, 14, false, _ => { }, (app, w, screen) =>
        {
            var top = w.Landing.WordmarkRow!.Value;
            Assert.True(top >= 0);
            Assert.Equal(w.Composer.Frame.Y - 1, top + TuiHeader.WordmarkRows);
            Assert.Contains("█▀▀█ █  █ █▀▀█", screen[top]);
            var left = (35 - TgWelcomeWordmark.Width) / 2;
            Assert.Equal("▀", app.Driver!.Contents![top + 3, left + 1].Grapheme);
            Assert.Equal(TgPalette.ToColor((13, 34, 38)), TgSnapshot.CellAttribute(app, top + 3, left + 1).Foreground);
            for (var row = w.Composer.Frame.Y; row < w.Composer.Frame.Bottom; row++)
                Assert.DoesNotContain(screen[row].PadRight(35).Substring(w.Composer.Frame.X, w.Composer.Frame.Width),
                    ch => ch is '█' or '▀' or '▄');
        });
    }

    [Theory]
    [InlineData(3, 12)]
    [InlineData(4, 31)]
    public void Welcome_paints_the_sweep_using_the_shared_welcome_clock(double time, int column)
    {
        Render(120, 36, false, _ => { }, (app, w, _) =>
        {
            var actual = TgSnapshot.CellAttribute(app, w.Landing.WordmarkRow!.Value,
                (120 - TgWelcomeWordmark.Width) / 2 + column);
            var expected = TgWelcomeWordmark.AttributeAt(0, column, TgWelcomeWordmark.SweepPosition(time));
            Assert.Equal(expected.Foreground, actual.Foreground);
            Assert.NotEqual(TgWelcomeWordmark.AttributeAt(0, column, double.NaN).Foreground, actual.Foreground);
        }, w =>
        {
            w.Landing.Tick(TimeSpan.Zero);
            var steps = (int)Math.Ceiling(time / 0.1);
            for (var i = 1; i <= steps; i++) w.Landing.Tick(TimeSpan.FromSeconds(time * i / steps));
        });
    }

    [Theory]
    [InlineData(13.095)]
    [InlineData(13.63)]
    public void Welcome_meteors_reach_below_the_sky_without_painting_input_or_hint(double time)
    {
        Render(160, 50, false, _ => { }, (app, w, screen) =>
        {
            Span<TuiShootingStars.Cell> cells = stackalloc TuiShootingStars.Cell[TuiShootingStars.MaxCells];
            var count = TuiShootingStars.Collect(cells, 160, 50, w.Landing.AnimationTime, out _);
            if (time < 13.1)
            {
                // The two-dot head can straddle two terminal cells.
                var heads = cells[..count].ToArray().Where(cell => cell.Row == 49 && cell.Color == 8).ToArray();
                Assert.NotEmpty(heads);
                foreach (var head in heads)
                {
                    var (background, _) = TuiWelcomeStars.At(head.Column, head.Row, w.Landing.AnimationTime);
                    Assert.Equal(((char)(0x2800 + (background | head.Dots))).ToString(),
                        app.Driver!.Contents![head.Row, head.Column].Grapheme);
                    Assert.Equal(TgPalette.ToColor((255, 255, 255)),
                        TgSnapshot.CellAttribute(app, head.Row, head.Column).Foreground);
                }
            }
            else Assert.Contains(cells[..count].ToArray(), cell => w.Composer.Frame.Contains(cell.Column, cell.Row));
            for (var row = w.Composer.Frame.Y; row < w.Composer.Frame.Bottom; row++)
                Assert.DoesNotContain(screen[row].PadRight(160).Substring(w.Composer.Frame.X, w.Composer.Frame.Width),
                    ch => ch is >= '\u2801' and <= '\u28ff');
            Assert.DoesNotContain(screen[w.Hints.Frame.Y], ch => ch is >= '\u2801' and <= '\u28ff');
        }, w =>
        {
            w.Landing.Tick(TimeSpan.Zero);
            var steps = (int)Math.Ceiling(time / 0.1);
            for (var i = 1; i <= steps; i++) w.Landing.Tick(TimeSpan.FromSeconds(time * i / steps));
        });
    }

    [Fact]
    public void Tall_welcome_draft_shrinks_sky_and_keeps_input_and_hint_on_screen()
    {
        Render(80, 24, false, w => w.Composer.Input.Text = string.Join('\n', Enumerable.Repeat("draft line", 8)),
            (_, w, _) =>
            {
                Assert.True(w.Composer.Frame.Y >= 0);
                Assert.True(w.Composer.Frame.Bottom <= w.Hints.Frame.Y);
                Assert.True(w.Hints.Frame.Bottom <= 24);
                Assert.InRange(w.Landing.SkyRows!.Value, 0, 16);
                Assert.True(w.Landing.WordmarkRow + TuiHeader.WordmarkRows < w.Composer.Frame.Y);
            });
    }

    [Fact]
    public void EmptyChatHasDefaultPlaceholderBottomPaddingAndQuietCommandHint()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Composer.SetMeta("Standard", "step-5-preview", "kilo");
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = TgSnapshot.Capture(app);
            var top = window.Composer.Frame.Y;
            Assert.Contains("Ask anything…", rows[top + 1]);
            Assert.True(window.Header.Visible);
            Assert.DoesNotContain("Standard", string.Join('\n', rows));
            Assert.DoesNotContain("Standard", rows[top + TgComposerView.MetadataRow]);
            Assert.DoesNotContain("Standard", rows[top + TgComposerView.RowCount - 1]);
            Assert.EndsWith("ctrl+p commands", rows[window.Hints.Frame.Y]);
            Assert.DoesNotContain("tab complete", rows[window.Hints.Frame.Y]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
    private static void Render(int columns, int rows, bool session, Action<TgChatWindow> configure,
        Action<IApplication, TgChatWindow, string[]> inspect, Action<TgChatWindow>? beforePaint = null)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Composer.SetMeta("Standard", "step-5-preview", "kilo");
        window.Composer.SetPlaceholder("Ask anything, / for commands…");
        window.Hints.SetIdle("D:\\LLM");
        window.Header.UpdateUsage(39413, 20, 0.29m);
        if (session) window.Transcript.Append("Inspect the parser", TranscriptStyle.Prose);
        configure(window);
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            app.Driver!.SetScreenSize(columns, rows);
            window.FocusComposer();
            beforePaint?.Invoke(window);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(350), () =>
        {
            Assert.Equal(columns, app.Driver!.Cols);
            Assert.Equal(rows, app.Driver.Rows);
            inspect(app, window, TgSnapshot.Capture(app));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void ComposerIsAFilledSurfaceWithoutAFrameOrRail()
    {
        Render(120, 30, true, _ => {}, (app, w, rows) =>
        {
            var top = w.Composer.Frame.Y;
            var left = w.Composer.Frame.X;
            var surface = TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()).Background;
            Assert.Contains("Standard", rows[w.Header.Frame.Y], StringComparison.Ordinal);
            Assert.DoesNotContain("Standard", rows[top + TgComposerView.MetadataRow], StringComparison.Ordinal);
            Assert.DoesNotContain("┌", rows[top], StringComparison.Ordinal);
            Assert.Equal(surface, TgSnapshot.CellAttribute(app, top + 1, left + 20).Background);
            // The boundary is the fill itself: no frame column at the left edge.
            Assert.NotEqual("│", app.Driver!.Contents![top + 1, left].Grapheme);
            Assert.Equal(surface, TgSnapshot.CellAttribute(app, top + 1, left).Background);
        });
    }

    [Fact]
    public void CompactComposerKeepsABlockCursorAfterFocusAndEditing()
    {
        Render(120, 30, true, w => w.Composer.Input.Text = "Hello", (_, w, _) =>
        {
            w.Composer.Input.InsertionPoint = 2;
            Assert.Equal(CursorStyle.SteadyBlock, w.Composer.Input.Cursor.Style);
            Assert.Equal(TgComposerView.RowCount, w.Composer.Frame.Height);
        });
    }

    [Fact]
    public void WideSessionUsesTheFullConversationWidthWithContextInTheHeader()
    {
        Render(140, 36, true, _ => {}, (app, w, rows) =>
        {
            var text = string.Join('\n', rows);
            Assert.DoesNotContain("D:\\LLM", rows[0], StringComparison.Ordinal);
            Assert.DoesNotContain("Context", text, StringComparison.Ordinal);
            Assert.Contains("39,413", text, StringComparison.Ordinal);
            Assert.True(w.Transcript.Frame.X >= 2);
            Assert.Equal(app.Driver!.Cols - 4, w.Composer.Frame.Width);
            Assert.InRange(Math.Abs(w.Composer.Frame.X - (app.Driver!.Cols - w.Composer.Frame.Right)), 0, 1);
            Assert.Equal(3, w.Transcript.Frame.Y);
        });
    }

    [Fact]
    public void SlashPanelSharesTheFullComposerWidth()
    {
        Render(180, 30, true, w => w.Composer.Input.Text = "/", (app, w, rows) =>
        {
            Assert.Equal(176, w.Composer.Frame.Width);
            var title = Array.FindIndex(rows, row => row.Contains("commands", StringComparison.Ordinal)
                && row.Contains("esc", StringComparison.Ordinal));
            Assert.True(title >= 0);
            Assert.Equal(TgPalette.ToColor(TgPalette.CardBgRgb()),
                TgSnapshot.CellAttribute(app, title, w.Composer.Frame.X).Background);
            Assert.Equal(TgPalette.ToColor(TgPalette.CardBgRgb()),
                TgSnapshot.CellAttribute(app, title, w.Composer.Frame.Right - 1).Background);
            Assert.Equal(TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()).Background,
                TgSnapshot.CellAttribute(app, w.Composer.Frame.Y, w.Composer.Frame.X).Background);
            Assert.Equal(TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()).Background,
                TgSnapshot.CellAttribute(app, w.Composer.Frame.Y + 1, w.Composer.Frame.X + 10).Background);
            Assert.DoesNotContain("ru ·", rows[0]);
        });
    }

    [Fact]
    public void ModelPickerIsCentredOnTheWholeTerminal()
    {
        Render(180, 30, true,
            w => w.ShowPicker("models", [new RuAgent.Cli.Tui.ChoiceItem("model A", "details", "a")]),
            (app, _, rows) =>
            {
                var occupied = Enumerable.Range(0, app.Driver!.Rows)
                    .Where(row => Enumerable.Range(0, app.Driver.Cols).Any(column =>
                        TgSnapshot.CellAttribute(app, row, column).Background == TgPalette.ToColor(TgPalette.CardBgRgb())))
                    .ToArray();
                Assert.NotEmpty(occupied);
                Assert.InRange(Math.Abs(occupied[0] + occupied[^1] + 1 - app.Driver.Rows), 0, 1);
            });
    }

    [Fact]
    public void NarrowSessionKeepsConversationAndComposerInOneColumn()
    {
        Render(80, 24, true, _ => {}, (app, w, rows) =>
        {
            Assert.DoesNotContain("Context", string.Join('\n', rows), StringComparison.Ordinal);
            Assert.Equal(2, w.Transcript.Frame.X);
            Assert.Equal(2, w.Composer.Frame.X);
            Assert.Equal(78, w.Composer.Frame.Right);
            Assert.Contains("step-5-preview", rows[w.Composer.Frame.Y + TgComposerView.MetadataRow], StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 30)]
    [InlineData(160, 40)]
    public void HomeKeepsComposerCentredHorizontallyBelowTheSky(int columns, int rows)
    {
        Render(columns, rows, false, _ => {}, (_, w, _) =>
        {
            Assert.InRange(w.Composer.Frame.Width, 20, 80);
            Assert.InRange(Math.Abs(w.Composer.Frame.X - (columns - w.Composer.Frame.Right)), 0, 1);
            Assert.True(w.Composer.Frame.Bottom <= rows - 2);
            Assert.True(w.Hints.Frame.Bottom <= rows - 1);
            Assert.True(w.Header.Visible);
            Assert.False(w.Header.ShowSessionDetails);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandOverlaysFitTheSmallestActiveFrame(bool slash)
    {
        Render(20, 12, false, w =>
        {
            if (slash) w.Composer.Input.Text = "/";
            else w.OpenCommandPalette();
        }, (app, w, rows) =>
        {
            Assert.Contains("commands", string.Join('\n', rows), StringComparison.OrdinalIgnoreCase);
            if (slash)
            {
                // The dropdown sits above the composer, so the block's fill stays visible
                // along with the preset on the meta row.
                Assert.Equal(
                    TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()).Background,
                    TgSnapshot.CellAttribute(app, w.Composer.Frame.Y, w.Composer.Frame.X + 1).Background);
                // At 20x12 the open command list owns the cells above the composer,
                // including the header. Permission state survives that temporary overlay.
                Assert.Equal("Standard", w.Header.Preset);
                Assert.DoesNotContain("Standard", rows[w.Composer.Frame.Y + TgComposerView.MetadataRow]);
            }
            else
            {
                // The palette strip spans the composer at this size, so the overlay owns the
                // frame cells; the composer itself keeps its meta.
                Assert.Equal(
                    TgPalette.Attr(TuiToken.Ink).Background,
                    TgSnapshot.CellAttribute(app, w.Composer.Frame.Y, w.Composer.Frame.X + 1).Background);
                Assert.Equal("Standard", w.Composer.Preset);
            }
        });
    }

    [Fact]
    public void LongMetadataDoesNotOverwriteInputOrUserSurface()
    {
        Render(80, 24, true, w =>
        {
            w.Composer.SetMeta("Standard", new string('界', 100), new string('p', 100));
            w.Composer.Input.Text = "Giữ nguyên câu đang gõ";
            w.Transcript.Append("Short question", TranscriptStyle.User);
        }, (app, w, rows) =>
        {
            Assert.Contains("Giữ nguyên câu đang gõ", rows[w.Composer.Frame.Y + 1]);
            Assert.Contains("Standard", rows[w.Header.Frame.Y]);
                Assert.DoesNotContain("Standard", rows[w.Composer.Frame.Y + TgComposerView.MetadataRow]);
            // The bottom of the block is still the filled surface.
            Assert.Equal(TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()).Background,
                TgSnapshot.CellAttribute(app, w.Composer.Frame.Bottom - 1, w.Composer.Frame.X + 1).Background);
            // The user block keeps its own element tone.
            Assert.Equal(TgPalette.ToColor(TuiPalette.ElementRgb()),
                TgSnapshot.CellAttribute(app, w.Transcript.Frame.Y + 1, w.Transcript.Frame.Right - 1).Background);
        });
    }
}
