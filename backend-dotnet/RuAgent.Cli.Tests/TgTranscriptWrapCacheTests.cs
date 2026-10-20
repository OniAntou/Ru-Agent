using System.Drawing;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgTranscriptWrapCacheTests
{
    [Fact]
    public void Identical_text_keeps_each_rows_own_paint_after_rebuild_and_resize()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var view = new TgTranscriptView { Width = 20, Height = 6 };
        window.Add(view);
        const string text = "cached text";
        view.Append(text, TranscriptStyle.Prose, [new TgSpan(text, TgPalette.Attr(TuiToken.Err))]);
        view.Append(text, TranscriptStyle.Prose, [new TgSpan(text, TgPalette.Attr(TuiToken.Ok))]);
        view.Append("a | b", TranscriptStyle.Prose);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            Assert.Equal(TgPalette.ToColor(TuiToken.Err), TgSnapshot.CellAttribute(app, 0, 0).Foreground);
            Assert.Equal(TgPalette.ToColor(TuiToken.Ok), TgSnapshot.CellAttribute(app, 1, 0).Foreground);
            view.Width = 10;
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(450), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Equal("cached", rows[0]);
            Assert.Equal("text", rows[1]);
            Assert.Equal("cached", rows[2]);
            Assert.Equal("text", rows[3]);
            Assert.Equal(TgPalette.ToColor(TuiToken.Err), TgSnapshot.CellAttribute(app, 1, 0).Foreground);
            Assert.Equal(TgPalette.ToColor(TuiToken.Ok), TgSnapshot.CellAttribute(app, 3, 0).Foreground);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Streaming_tail_reuses_wrapped_history_with_pipes_or_expanded_details(bool expanded)
    {
        using var view = new TgTranscriptView { Frame = new Rectangle(0, 0, 100, 25) };
        for (var i = 0; i < 1800; i++) view.Append($"{i:D4} " + new string('a', 75), TranscriptStyle.Prose);
        if (expanded)
        {
            view.AppendCollapsed("tool summary", ["detail one", "detail two"], TranscriptStyle.Tool);
            view.ToggleExpanded();
        }
        view.Append("tail", TranscriptStyle.Prose);
        Assert.True(view.CanScroll);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 150; i++)
        {
            view.ReplaceTail((expanded ? "alpha " : "a | b ") + i.ToString("D4") + new string('x', 110), TranscriptStyle.Prose);
            Assert.True(view.CanScroll);
        }

        var used = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(used < 32L * 1024 * 1024,
            $"Updating the tail allocated {used / 1048576d:F2} MiB; unchanged history must reuse its layout.");
        Assert.EndsWith("0149" + new string('x', 110), view.Lines[^1].Text);
        if (expanded) Assert.Equal(["detail one", "detail two"], view.CollapsedRows.Select(row => row.Text));
    }

    [Fact]
    public void Reused_history_reflows_after_resize_and_preserves_detached_scroll()
    {
        using var view = new TgTranscriptView { Frame = new Rectangle(0, 0, 100, 5) };
        var text = new string('a', 80);
        view.Append(text, TranscriptStyle.Prose);
        Assert.False(view.CanScroll);
        view.Frame = new Rectangle(0, 0, 10, 5);
        Assert.True(view.CanScroll);
        view.ScrollToTop();
        view.Append("a | b", TranscriptStyle.Prose);
        for (var i = 0; i < 5; i++)
        {
            view.ReplaceTail("a | b " + i, TranscriptStyle.Prose);
            Assert.Equal(0, view.ScrollPosition);
        }
        Assert.False(view.IsFollowingTail);
        view.Frame = new Rectangle(0, 0, 100, 5);
        Assert.False(view.CanScroll);
        Assert.Equal(text, view.Lines[0].Text);
    }
}
