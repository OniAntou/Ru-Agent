using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgPresetQueueTests
{
    [Fact]
    public void Preset_cannot_bypass_a_paused_queue_as_a_repair_command()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        loop.SetStateChangingCommandClassifier(line => line.StartsWith('/'));
        loop.SetPromptHandler(async (line, _, ct) =>
        {
            seen.Add(line);
            if (line == "active") await release.Task.WaitAsync(ct);
        });
        var started = false;
        var checkedQueue = false;
        Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                if (!started)
                {
                    window.Composer.Input.Text = "active";
                    loop.Submit();
                    window.Composer.Input.Text = "queued";
                    loop.Submit();
                    loop.PauseQueuedSubmissions();
                    release.TrySetResult();
                    started = true;
                    return true;
                }
                if (window.Hints.ActiveState != TgHintsState.Paused) return true;
                Assert.Equal(1, window.Hints.QueuedCount);
                window.Composer.Input.Text = "/preset plan";
                loop.Submit();
                Assert.Equal("/preset plan", window.Composer.Input.Text);
                Assert.Equal(["active"], seen);
                Assert.True(window.Hints.FeedbackIsError);
                checkedQueue = true;
            }
            catch (Exception error) { failure = error; }
            loop.Stop();
            app.RequestStop();
            return false;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () =>
        {
            loop.Stop();
            app.RequestStop();
            return false;
        });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(checkedQueue);
    }
}
