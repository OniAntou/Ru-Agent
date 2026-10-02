using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgTranscriptTests
{
    private static string[] Render(int rows, Action<TgTranscriptView> act)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var transcript = new TgTranscriptView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(transcript);
        act(transcript);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { app.RequestStop(); return false; });
        app.Run(window);
        return TgSnapshot.Capture(app);
    }

    [Fact]
    public void Overflow_PinsTheTail()
    {
        var rows = Render(24, t =>
        {
            for (var i = 0; i < 60; i++)
            {
                t.Append($" assistant line {i:D2}", TranscriptStyle.Prose);
            }
        });

        Assert.Contains("assistant line 59", rows[^1]);
        Assert.DoesNotContain("assistant line 00", string.Join('\n', rows));
    }

    [Fact]
    public void AppendChunk_GrowsTheSameLine()
    {
        var rows = Render(24, t =>
        {
            t.Append("Em ", TranscriptStyle.Prose);
            t.AppendChunk("kiểm tra ");
            t.AppendChunk("lock.");
        });

        Assert.Contains("Em kiểm tra lock.", rows[0]);
    }

    [Fact]
    public void LongLine_WrapsInsideTheViewport()
    {
        var rows = Render(24, t => t.Append(new string('x', 200), TranscriptStyle.Prose));

        Assert.Contains("x", rows[0]);
        Assert.Contains("x", rows[1]);
    }

    [Fact]
    public void AppendAfterFirstDraw_WrapsAndPinsTheTail()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var transcript = new TgTranscriptView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(transcript);
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            transcript.Append("first paint", TranscriptStyle.Prose);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            for (var i = 0; i < 60; i++)
            {
                transcript.Append($" late line {i:D2}", TranscriptStyle.Prose);
            }

            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Contains("late line 59", rows[^1]);
            Assert.DoesNotContain("late line 00", string.Join('\n', rows));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void LongLine_HardWrapsWithoutLosingText()
    {
        var text = new string('x', 200);
        var rows = Render(24, t => t.Append(text, TranscriptStyle.Prose));

        var joined = string.Join(string.Empty, rows);
        Assert.DoesNotContain("…", joined);
        Assert.Contains(text, joined);
    }

    [Fact]
    public void EmptyLine_OccupiesOneRow()
    {
        var rows = Render(24, t =>
        {
            t.Append("top", TranscriptStyle.Prose);
            t.Append(string.Empty, TranscriptStyle.Prose);
            t.Append("bottom", TranscriptStyle.Prose);
        });

        Assert.Contains("top", rows[0]);
        Assert.Equal(string.Empty, rows[1].TrimEnd());
        Assert.Contains("bottom", rows[2]);
    }
}
