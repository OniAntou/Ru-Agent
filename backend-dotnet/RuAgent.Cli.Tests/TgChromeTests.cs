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
    public void HeaderActivityUpdatesStayAboveTheFeedAndClearThePreviousLabel()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Hints.SetIdle("D:\\LLM");
        window.Header.SetActivity("Reading a-long-file-name.cs");
        window.Transcript.Append("answer content", TranscriptStyle.Prose);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            Assert.Contains("Reading a-long-file-name.cs", TgSnapshot.Capture(app)[2]);
            window.Header.SetActivity("idle");
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Contains("D:\\LLM", rows[1]);
            Assert.StartsWith("  idle", rows[2]);
            Assert.DoesNotContain("a-long-file-name.cs", rows[2]);
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
            Assert.StartsWith("# session", TgSnapshot.Capture(app)[0], StringComparison.Ordinal);

            // A named session replaces the placeholder rather than appending to it. The
            // repaint lands on the next loop turn, so the capture has to wait for it.
            header.SetTitle("Fix the busy lock");
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            Assert.StartsWith("# Fix the busy lock", TgSnapshot.Capture(app)[0], StringComparison.Ordinal);

            // A blank title falls back rather than leaving a lone "#".
            header.SetTitle("   ");
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            Assert.StartsWith("# session", TgSnapshot.Capture(app)[0], StringComparison.Ordinal);
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
            Assert.StartsWith("# Fix the busy lock", row);
            Assert.Contains("39,413", row);
            Assert.Contains("20%", row);
            Assert.Contains("$0.29", row);
            var hash = row.IndexOf('#');
            Assert.Equal(TgPalette.ToColor(TuiToken.Brand), TgSnapshot.CellAttribute(app, 0, hash).Foreground);
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
    /// Task 5's parity minors: the tally emits a part only when TuiHeader.Compose would —
    /// tokens when positive, the percent when known, the cost when priced.
    /// </summary>
    [Fact]
    public void ComposeUsage_OmitsTokensWhenNotPositive_AndPercentWhenNull()
    {
        Assert.Equal("39,413  20%  ($0.29)", TgHeaderView.ComposeUsage(39413, 20, 0.29m));
        Assert.Equal("20%", TgHeaderView.ComposeUsage(0, 20, null));
        Assert.Equal("39,413", TgHeaderView.ComposeUsage(39413, null, null));
        Assert.Equal(string.Empty, TgHeaderView.ComposeUsage(0, null, null));
        Assert.Equal(string.Empty, TgHeaderView.ComposeUsage(-1, null, null));
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
            Assert.Contains("┃", rows[window.Composer.Frame.Y]);
            Assert.Contains("/ commands", rows[^1]);
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
                    Assert.Contains("┃", rows[window.Composer.Frame.Y]);
                    Assert.Contains("/ commands", rows[^1]);
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
                Assert.Contains("┃", rows[window.Composer.Frame.Y]);
                Assert.Contains("/ commands", rows[^1]);
                app.RequestStop();
            });
            return false;
        });
        app.Run(window);
        Assert.False(window.HandOverToSequential);
    }

    [Fact]
    public void Hints_IdleShowsWorkspaceAndKeys_BusyShowsSpinner()
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
            Assert.Contains("C:/Users/USER", row);
            Assert.Contains("/ commands", row);
            Assert.Contains("ctrl+o", row);
            var words = row.IndexOf("/ commands", StringComparison.Ordinal);
            Assert.Equal(TgPalette.ToColor(TuiToken.Meta), TgSnapshot.CellAttribute(app, 0, words + 2).Foreground);
            var tab = row.IndexOf("tab", StringComparison.Ordinal);
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, tab).Foreground);
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
    public void Hints_BusyShowsSpinnerAndInterrupt()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var hints = new TgHintsView { X = 0, Y = 0, Width = Dim.Fill() };
        hints.SetBusy("⠋");
        window.Add(hints);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var row = TgSnapshot.Capture(app)[0];
            Assert.Contains("⠋ esc interrupt", row);
            var spinner = row.IndexOf('⠋');
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, spinner).Foreground);
            var esc = row.IndexOf("esc", StringComparison.Ordinal);
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, esc).Foreground);
            Assert.Equal(TgPalette.ToColor(TuiToken.Meta), TgSnapshot.CellAttribute(app, 0, esc + 4).Foreground);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
