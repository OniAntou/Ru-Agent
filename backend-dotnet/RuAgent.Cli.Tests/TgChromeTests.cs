using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgChromeTests
{
    [Fact]
    public void LiveActivityRowSitsAtTheTailAndClearsWhenSettled()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Hints.SetIdle("D:\\LLM");
        window.Transcript.Append("answer content", TranscriptStyle.Prose);
        window.Transcript.UpdateLive(new TuiActivityPresentation(
            "◐", "Reading", "a-long-file-name.cs", "1.2s", TuiToken.Brand));
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = TgSnapshot.Capture(app);
            var liveRow = rows[window.Transcript.Frame.Y + 1];
            Assert.Contains("Reading", liveRow);
            Assert.Contains("a-long-file-name.cs", liveRow);
            Assert.Contains("1.2s", liveRow);
            // The live row is the running form of the settled row: its glyph carries the
            // activity tone, and the metrics flush right like every tool row. Cell
            // coordinates are window-relative, so the view's own X offset applies.
            var liveRowIndex = window.Transcript.Frame.Y + 1;
            var glyph = TgSnapshot.CellAttribute(app, liveRowIndex, window.Transcript.Frame.X).Foreground;
            Assert.True(
                TgPalette.ToColor(TuiToken.Brand) == glyph,
                $"glyph foreground: expected {TgPalette.ToColor(TuiToken.Brand)} actual {glyph}");
            Assert.EndsWith("1.2s", liveRow.TrimEnd());
            window.Transcript.ClearLive();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.DoesNotContain("a-long-file-name.cs", string.Join('\n', rows));
            Assert.Contains("answer content", rows[window.Transcript.Frame.Y]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>
    /// Drives the screen to a size and only continues once the driver reports it. The test
    /// driver silently refuses a <c>SetScreenSize</c> it has not settled on yet, and a resize
    /// test that ignores that passes for the wrong reason: the frame stayed up because the
    /// screen never changed at all.
    /// </summary>
    private static void ResizeTo(IApplication app, int width, int height, Action then, int attempts = 60)
    {
        var left = attempts;
        void Step()
        {
            if (app.Driver!.Cols == width && app.Driver.Rows == height)
            {
                then();
                return;
            }
            if (left-- <= 0)
            {
                Assert.Equal(width, app.Driver.Cols);
                Assert.Equal(height, app.Driver.Rows);
                return;
            }

            app.Driver.SetScreenSize(width, height);
            app.AddTimeout(TimeSpan.FromMilliseconds(25), () =>
            {
                Step();
                return false;
            });
        }

        Step();
    }

    /// <summary>
    /// The wordmark centres itself from its viewport, so it has to stay centred after a resize
    /// at every size the operator can drag to. Every frame after the resize is checked rather
    /// than only the settled one, since a layout problem here is transient by nature.
    /// <para>
    /// This does NOT reproduce the stale-viewport report: the test driver reverts a resize
    /// before the stale frame can be sampled, so the test passes with either a scheduled or a
    /// synchronous relayout. It guards centring across sizes, not the ordering.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(120, 30)]
    [InlineData(60, 20)]
    [InlineData(45, 14)]
    public void Landing_KeepsTheWordmarkCentredInEveryFrameAfterAResize(int width, int height)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
        {
            ResizeTo(app, width, height, () =>
            {
                var watch = 12;
                void Watch()
                {
                    var rows = TgSnapshot.Capture(app);
                    var cols = app.Driver!.Cols;
                    var (left, right) = WordmarkBounds(rows);
                    if (left != int.MaxValue)
                    {
                        Assert.True(
                            Math.Abs(left - (cols - right - 1)) <= 1,
                            $"wordmark lech o {cols}x{app.Driver.Rows}: trai={left} phai={cols - right - 1}");
                    }

                    if (watch-- <= 0)
                    {
                        app.RequestStop();
                        return;
                    }

                    app.AddTimeout(TimeSpan.FromMilliseconds(16), () =>
                    {
                        Watch();
                        return false;
                    });
                }

                Watch();
            });
            return false;
        });
        app.Run(window);
    }

    private static (int Left, int Right) WordmarkBounds(string[] rows)
    {
        var left = int.MaxValue;
        var right = -1;
        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                if (rows[r][c] is '█' or '▀')
                {
                    left = Math.Min(left, c);
                    right = Math.Max(right, c);
                }
            }
        }

        return (left, right);
    }

    [Fact]
    public void Header_NamesTheSessionUntilTheHostSuppliesATitle()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var header = new TgHeaderView { X = 0, Y = 0, Width = Dim.Fill() };
        window.Add(header);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            Assert.StartsWith("session", TgSnapshot.Capture(app)[0], StringComparison.Ordinal);

            // A named session replaces the placeholder rather than appending to it. The
            // repaint lands on the next loop turn, so the capture has to wait for it.
            header.SetTitle("Fix the busy lock");
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            Assert.StartsWith("Fix the busy lock", TgSnapshot.Capture(app)[0], StringComparison.Ordinal);

            // A blank title falls back rather than leaving a lone "#".
            header.SetTitle("   ");
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            Assert.StartsWith("session", TgSnapshot.Capture(app)[0], StringComparison.Ordinal);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void SettledToolRowReplacesTheLiveActivityRow()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var writer = new TgTranscriptWriter(app, window.Transcript);
            window.Transcript.UpdateLive(new TuiActivityPresentation(
                "◐", "Reading", "lock.cs", "1s", TuiToken.Brand));
            Assert.NotNull(window.Transcript.Live);

            writer.CommitLine(new[] { "✓ Read lock.cs  0.2s" });

            Assert.Null(window.Transcript.Live);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            // The committed row is drawn on the next loop iteration, like every append.
            Assert.Contains("✓ Read lock.cs", string.Join('\n', TgSnapshot.Capture(app)));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void UserBlock_FillsThreeRowsAndPaintsThePromptChevron()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Transcript.Append(string.Empty, TranscriptStyle.User);
        window.Transcript.Append("  ❯ sửa lỗi busy lock", TranscriptStyle.User);
        window.Transcript.Append(string.Empty, TranscriptStyle.User);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = TgSnapshot.Capture(app);
            var top = window.Transcript.Frame.Y;
            var right = window.Transcript.Frame.Right - 1;
            var fill = TgPalette.ToColor(TuiPalette.ElementRgb());
            var padTop = TgSnapshot.CellAttribute(app, top, right).Background;
            var textBg = TgSnapshot.CellAttribute(app, top + 1, right).Background;
            var padBottom = TgSnapshot.CellAttribute(app, top + 2, right).Background;
            Assert.True(fill == padTop, $"pad top background: expected {fill} actual {padTop}");
            Assert.True(fill == textBg, $"text background: expected {fill} actual {textBg}");
            Assert.True(fill == padBottom, $"pad bottom background: expected {fill} actual {padBottom}");
            var chevron = TgSnapshot.CellAttribute(app, top + 1, window.Transcript.Frame.X + 2).Foreground;
            Assert.True(
                TgPalette.ToColor(TuiToken.Ok) == chevron,
                $"chevron foreground: expected {TgPalette.ToColor(TuiToken.Ok)} actual {chevron}");
            Assert.Contains("sửa lỗi busy lock", rows[top + 1]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void HintsWaveClock_AdvancesWhileBusyOnly()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Hints.SetIdle("D:\\LLM");
        window.Transcript.Append("answer content", TranscriptStyle.Prose);
        window.Hints.SetBusy();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            window.Hints.SetWaveClock(0.5);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            Assert.Contains("▬▬▬▬▬ Working", TgSnapshot.Capture(app)[^1]);
            window.Hints.SetIdle("D:\\LLM");
            window.Hints.SetWaveClock(1.0);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            Assert.Contains("Ready", TgSnapshot.Capture(app)[^1]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Header_ShowsTitleAndUsageCluster()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var header = new TgHeaderView { X = 0, Y = 0, Width = Dim.Fill() };
        header.SetTitle("Fix the busy lock");
        header.UpdateUsage(totalTokens: 39413, percent: 20, cost: 0.29m);
        window.Add(header);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var row = TgSnapshot.Capture(app)[0];
            // Classic TuiHeader starts the heading at column 0: "# title", no leading pad.
            Assert.StartsWith("Fix the busy lock", row);
            Assert.Contains("39,413", row);
            Assert.Contains("20.00%", row);
            Assert.Contains("$0.29", row);
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, 0).Foreground);
            var tally = row.IndexOf("39,413", StringComparison.Ordinal);
            Assert.Equal(TgPalette.ToColor(TuiToken.Meta), TgSnapshot.CellAttribute(app, 0, tally).Foreground);
            // Flush right like TuiHeader's TwoColumn: the cluster ends on the last column.
            var last = app.Driver!.Cols - 1;
            Assert.Equal(")", app.Driver.Contents![0, last].Grapheme);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>
    /// The context meter is cells, not glyphs: the fill run carries the fill
    /// tone on its own background and the rest of the track keeps the dark
    /// surface behind it.
    /// </summary>
    [Fact]
    public void Header_MeterPaintsFillAndTrackCells()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var header = new TgHeaderView { X = 0, Y = 0, Width = Dim.Fill() };
        header.UpdateUsage(totalTokens: 39413, percent: 20, cost: 0.29m);
        window.Add(header);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var row = TgSnapshot.Capture(app)[0];
            Assert.Contains("20.00%", row);

            // "39,413" · 2 · meter 8 · 1 · "20.00%" · 2 · "($0.29)" flush right.
            var last = app.Driver!.Cols - 1;
            var meterStart = last + 1 - "($0.29)".Length - 2 - "20.00%".Length - 1 - TuiUsageMeter.BarWidth;
            Assert.Equal(" ", app.Driver.Contents![0, meterStart].Grapheme);

            // 20% lights two of the eight cells in cream; the rest stays track.
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, meterStart).Background);
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, meterStart + 1).Background);
            Assert.Equal(
                TgPalette.ToColor(TuiPalette.ElementRgb()),
                TgSnapshot.CellAttribute(app, 0, meterStart + 2).Background);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Window_KeepsComposerPinnedAtTheBottomAfterResize()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Transcript.Append("Active session", TranscriptStyle.Prose);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            app.Driver!.SetScreenSize(100, 30);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Equal(
                TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()).Background,
                TgSnapshot.CellAttribute(app, window.Composer.Frame.Y, window.Composer.Frame.X + 1).Background);
            Assert.Contains("ctrl+p commands", rows[^1]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        // 100x30 is still above the 80x24 floor: the TG frame must not hand over.
        Assert.False(window.HandOverToSequential);
    }

    /// <summary>
    /// The handover floor, pinned directly rather than through the screen. The test driver
    /// silently refuses the very small sizes a handover needs, so driving the app to them makes
    /// for a test that proves nothing; the rule itself is pure arithmetic and belongs here.
    /// </summary>
    [Theory]
    [InlineData(80, 24, false)]
    [InlineData(79, 23, false)]
    [InlineData(60, 20, false)]
    [InlineData(45, 14, false)]
    [InlineData(24, 13, false)]
    [InlineData(20, 12, false)]
    [InlineData(20, 11, true)]
    [InlineData(19, 12, true)]
    [InlineData(16, 9, true)]
    public void Handover_FiresOnlyBelowTheSizeTheLayoutCanDraw(int width, int height, bool tooSmall)
    {
        // Anything the startup engine floor accepts, and a good deal below it, must keep the
        // Terminal.Gui frame: an operator narrowing the window is not asking to change engine.
        Assert.Equal(tooSmall, TgChatWindow.IsTooSmallToDraw(width, height));
    }

    /// <summary>
    /// Dragging a window edge sweeps the width through small values for a moment. The handover
    /// is one-way for the life of the window, so a terminal that only dips below the drawable
    /// floor must keep the Terminal.Gui surface instead of losing it permanently.
    /// </summary>
    [Fact]
    public void Window_KeepsTheFrameWhenTheTerminalOnlyBlinksBelowTheFloor()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Transcript.Append("Active session", TranscriptStyle.Prose);
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
        {
            // 20x11 is one row under the drawable floor, and the smallest the driver accepts.
            ResizeTo(app, 20, 11, () =>
                ResizeTo(app, 110, 34, () =>
                {
                    Assert.False(window.HandOverToSequential);
                    // The composer is still the last region on screen: the frame never went away.
                    var rows = TgSnapshot.Capture(app);
                    Assert.Equal(
                        TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()).Background,
                        TgSnapshot.CellAttribute(app, window.Composer.Frame.Y, window.Composer.Frame.X + 1).Background);
                    Assert.Contains("ctrl+p commands", rows[^1]);
                    app.RequestStop();
                }));
            return false;
        });
        app.Run(window);
        Assert.False(window.HandOverToSequential);
    }

    /// <summary>
    /// The regression: the handover used to use the same 80x24 floor that selects the engine at
    /// startup, so narrowing the window below 80 columns tore down the whole surface for good,
    /// even though the layout still draws perfectly well at 60x20. Only a size the layout
    /// genuinely cannot fit may hand over.
    /// </summary>
    [Theory]
    [InlineData(60, 20)]
    [InlineData(79, 23)]
    [InlineData(45, 14)]
    [InlineData(120, 24)]
    public void Window_KeepsTheFrameBelowTheStartupEngineFloor(int width, int height)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Transcript.Append("Active session", TranscriptStyle.Prose);
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
        {
            ResizeTo(app, width, height, () =>
            {
                Assert.Equal(width, app.Driver!.Cols);
                Assert.Equal(height, app.Driver.Rows);
                Assert.False(window.HandOverToSequential);
                var rows = TgSnapshot.Capture(app);
                Assert.Equal(
                    TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()).Background,
                    TgSnapshot.CellAttribute(app, window.Composer.Frame.Y, window.Composer.Frame.X + 1).Background);
                Assert.Contains("ctrl+p commands", rows[^1]);
                app.RequestStop();
            });
            return false;
        });
        app.Run(window);
        Assert.False(window.HandOverToSequential);
    }

    [Fact]
    public void Hints_IdleShowsReadyAndKeys_BusyShowsSpinner()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var hints = new TgHintsView { X = 0, Y = 0, Width = Dim.Fill() };
        hints.SetIdle("C:/Users/USER");
        window.Add(hints);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var row = TgSnapshot.Capture(app)[0];
            Assert.Equal("C:/Users/USER", hints.Workspace);
            Assert.Contains("Ready", row);
            Assert.Contains("ctrl+p commands", row);
            var words = row.IndexOf("ctrl+p commands", StringComparison.Ordinal);
            Assert.Equal(
                TgPalette.ToColor(TuiToken.Meta),
                TgSnapshot.CellAttribute(app, 0, words + "ctrl+p ".Length + 2).Foreground);
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, words).Foreground);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>
    /// I3: `/clear` in TG mode resets the transcript and the header usage cluster in place
    /// while the title and the pinned regions stay.
    /// </summary>
    [Fact]
    public void ResetTranscript_ClearsRowsAndUsageCluster()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Header.SetTitle("Fix the busy lock");
        window.Header.UpdateUsage(39413, 20, 0.29m);
        window.Transcript.Append(" ▌ hello", TranscriptStyle.User);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            window.ResetTranscript();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Equal("Fix the busy lock", window.Header.SessionTitle);
            Assert.False(window.Header.Visible);
            Assert.DoesNotContain("hello", text);
            Assert.DoesNotContain("39,413", text);
            Assert.DoesNotContain("$0.29", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Hints_BusyShowsTheColourWaveAndInterrupt()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var hints = new TgHintsView { X = 0, Y = 0, Width = Dim.Fill() };
        hints.SetBusy();
        window.Add(hints);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var row = TgSnapshot.Capture(app)[0];
            Assert.Contains("▬▬▬▬▬ Working", row);
            // Each cell carries its own ramp colour: the wave is a line of distinct
            // cells, not one glyph with one tone.
            var first = row.IndexOf('▬');
            var cells = Enumerable.Range(0, TuiSpinner.WaveCells)
                .Select(cell => TgSnapshot.CellAttribute(app, 0, first + cell).Foreground)
                .ToArray();
            for (var cell = 0; cell < cells.Length; cell++)
            {
                Assert.Equal(TgPalette.ToColor(TuiSpinner.WaveRgbAt(0, TuiSpinner.WaveCycleSeconds - cell * TuiSpinner.WaveCycleSeconds / 5)), cells[cell]);
            }

            Assert.NotEqual(cells[0], cells[1]);
            var esc = row.IndexOf("esc", StringComparison.Ordinal);
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, esc).Foreground);
            Assert.Equal(TgPalette.ToColor(TuiToken.Meta), TgSnapshot.CellAttribute(app, 0, esc + 4).Foreground);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
