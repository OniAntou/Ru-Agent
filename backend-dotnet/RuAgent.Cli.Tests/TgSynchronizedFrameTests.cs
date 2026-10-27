using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgSynchronizedFrameTests
{
    [Fact]
    public void Repeated_begin_and_dispose_release_an_aborted_frame_exactly_once()
    {
        using IApplication app = Application.Create();
        var writes = new List<string>();
        var frame = new TgSynchronizedFrame(app, writes.Add);
        frame.Begin(); frame.Begin();
        Assert.True(frame.IsOpen);
        Assert.Equal(new[] { TgSynchronizedFrame.BeginSequence }, writes);
        frame.Dispose(); frame.Dispose(); frame.Begin();
        Assert.False(frame.IsOpen);
        Assert.Equal(new[] { TgSynchronizedFrame.BeginSequence, TgSynchronizedFrame.EndSequence }, writes);
    }

    [Fact]
    public void An_unused_frame_does_not_write_terminal_modes()
    {
        using IApplication app = Application.Create();
        var writes = new List<string>();
        using (var frame = new TgSynchronizedFrame(app, writes.Add)) { }
        Assert.Empty(writes);
    }

    [Fact]
    public void Completion_releases_only_after_the_real_driver_has_flushed_the_draw()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = new Terminal.Gui.Views.Window();
        var label = new Terminal.Gui.Views.Label { Text = "synchronized-frame-marker", Width = 30 };
        window.Add(label);
        var writes = new List<string>();
        string? flushed = null;
        using var frame = new TgSynchronizedFrame(app, sequence =>
        {
            if (sequence == TgSynchronizedFrame.EndSequence) flushed = app.Driver!.GetOutput().GetLastOutput();
            writes.Add(sequence);
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            label.Text = "synchronized-frame-updated";
            frame.Begin(); frame.Begin(); label.SetNeedsDraw(); return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(450), () => { app.RequestStop(); return false; });
        app.Run(window);
        Assert.False(frame.IsOpen);
        Assert.Equal(new[] { TgSynchronizedFrame.BeginSequence, TgSynchronizedFrame.EndSequence }, writes);
        Assert.Contains("synchronized-frame-updated", flushed);
    }
}
