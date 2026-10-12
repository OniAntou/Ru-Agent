using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// A workspace left busy by an orphaned run refuses every later prompt with a 409 and is silent
/// about it: the run holding the lease says nothing, and the advice used to live only in a
/// transcript line that scrolls away. The hint row now carries it, so these pin that the banner
/// names the run, that it survives more output, and that it clears once the block is gone.
/// </summary>
public sealed class TgWorkspaceBusyTests
{
    [Fact]
    public void Banner_NamesTheBlockingRunAndTheReleaseCommand()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();

        Run(
            app,
            window,
            [
                (120, w => w.Hints.SetWorkspaceBusy("run_abcdef0123456789", "running")),
                (80, w =>
                {
                    var text = string.Join('\n', TgSnapshot.Capture(app));
                    Assert.Contains("run_abcdef0123456789", text, StringComparison.Ordinal);
                    Assert.Contains("agent cancel run_abcdef0123456789", text, StringComparison.Ordinal);
                }),
            ]);

        app.Run(window);
    }

    [Fact]
    public void Banner_StaysPutWhileMoreOutputArrives()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();

        Run(
            app,
            window,
            [
                (120, w => w.Hints.SetWorkspaceBusy("run_abcdef0123456789", "running")),
                (60, w =>
                {
                    for (var i = 0; i < 200; i++)
                    {
                        w.Transcript.Append($"output line {i}", TranscriptStyle.Prose);
                    }
                }),
                (100, w =>
                {
                    // The transcript now scrolls; the banner must not be something that scrolled
                    // away with it.
                    var text = string.Join('\n', TgSnapshot.Capture(app));
                    Assert.Contains("run_abcdef0123456789", text, StringComparison.Ordinal);
                }),
            ]);

        app.Run(window);
    }

    [Fact]
    public void Clearing_RemovesTheBanner()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();

        Run(
            app,
            window,
            [
                (120, w => w.Hints.SetWorkspaceBusy("run_abcdef0123456789", "running")),
                (60, w => w.Hints.ClearWorkspaceBusy()),
                (80, w => Assert.DoesNotContain(
                    "run_abcdef0123456789",
                    string.Join('\n', TgSnapshot.Capture(app)),
                    StringComparison.Ordinal)),
            ]);

        app.Run(window);
    }

    /// <summary>Runs the steps one per timeout tick and stops the app at the end.</summary>
    private static void Run(
        IApplication app,
        TgChatWindow window,
        IReadOnlyList<(int DelayMs, Action<TgChatWindow> Step)> steps,
        int index = 0)
    {
        if (index >= steps.Count)
        {
            app.RequestStop();
            return;
        }

        var (delay, step) = steps[index];
        app.AddTimeout(TimeSpan.FromMilliseconds(delay), () =>
        {
            step(window);
            Run(app, window, steps, index + 1);
            return false;
        });
    }
}
