using System.Drawing;
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgWelcomeAnimationTests
{
    [Fact]
    public void Welcome_keeps_twenty_frame_budget_and_resumes_without_catching_up()
    {
        using var view = new TgLandingView { Frame = new Rectangle(0, 0, 120, 40) };
        var frames = 0;
        for (var milliseconds = 0; milliseconds <= 1000; milliseconds++)
        {
            var previous = view.AnimationTime;
            view.Tick(TimeSpan.FromMilliseconds(milliseconds));
            if (view.AnimationTime != previous) frames++;
        }
        Assert.Equal(20, frames);
        Assert.Equal(1, view.AnimationTime, 6);
        view.Visible = false;
        view.Tick(TimeSpan.FromSeconds(10));
        Assert.Equal(1, view.AnimationTime, 6);
        view.Visible = true;
        view.Tick(TimeSpan.FromSeconds(30));
        Assert.Equal(1, view.AnimationTime, 6);
        view.Tick(TimeSpan.FromSeconds(30.05));
        Assert.Equal(1.05, view.AnimationTime, 6);
    }

    [Fact]
    public void Welcome_has_no_fx_control_and_tab_keeps_the_draft_in_the_composer()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var complete = false; Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            try
            {
                if (app.AnsiStartupGate is { IsReady: false }) return true;
                window.FocusComposer(); window.Composer.Input.Text = "unsent draft";
                app.InjectKey(Key.Tab);
                Assert.True(window.Landing.Visible);
                Assert.Equal("unsent draft", window.Composer.Input.Text);
                Assert.Same(window.Composer.Input, window.MostFocused);
                Assert.DoesNotContain(window.SubViews, view => view.Text?.Contains("FX:", StringComparison.Ordinal) == true);
                Assert.DoesNotContain("FX:", string.Join('\n', TgSnapshot.Capture(app)));
                complete = true; app.RequestStop(); return false;
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { app.RequestStop(); return false; });
        app.Run(window); loop.Stop();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(complete);
    }
}
