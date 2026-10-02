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
}
