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
            Assert.Equal(TgPalette.ToColor(TuiToken.Bright), TgSnapshot.CellAttribute(app, 0, hash).Foreground);
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
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            app.Driver!.SetScreenSize(100, 30);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Contains("┌", rows[^7]);
            Assert.Contains("/ commands", rows[^1]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        // 100x30 is still above the 80x24 floor: the TG frame must not hand over.
        Assert.False(window.HandOverToSequential);
    }

    [Fact]
    public void Window_HandsOverToSequentialWhenTerminalStaysBelowTheMinimum()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var stillRunning = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            app.Driver!.SetScreenSize(79, 23);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(700), () =>
        {
            // The window must stop the app itself once the tiny size has settled; reaching
            // this timeout means RequestStop never ran.
            stillRunning = true;
            app.RequestStop();
            return false;
        });
        app.Run(window);
        Assert.False(stillRunning);
        Assert.True(window.HandOverToSequential);
    }

    /// <summary>
    /// Dragging a window edge sweeps the width through small values for a moment. The handover
    /// is one-way for the life of the window, so a terminal that only blinks below the 80x24
    /// floor must keep the Terminal.Gui surface instead of losing it permanently.
    /// </summary>
    [Fact]
    public void Window_KeepsTheFrameWhenTheTerminalOnlyBlinksBelowTheFloor()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            window.FocusComposer();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
        {
            app.Driver!.SetScreenSize(60, 20);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(320), () =>
        {
            app.Driver!.SetScreenSize(110, 34);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(900), () =>
        {
            Assert.False(window.HandOverToSequential);
            // The composer is still the last region on screen: the frame never went away.
            var rows = TgSnapshot.Capture(app);
            Assert.Contains("┌", rows[^7]);
            Assert.Contains("/ commands", rows[^1]);
            app.RequestStop();
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
            Assert.Contains("Fix the busy lock", text);
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
