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
    public void Welcome_settles_between_sweeps_after_one_cycle_and_wakes_on_input()
    {
        using var view = new TgLandingView { Frame = new Rectangle(0, 0, 120, 40) };
        for (var milliseconds = 0; milliseconds <= 12_000; milliseconds += 10)
            view.Tick(TimeSpan.FromMilliseconds(milliseconds));
        Assert.True(view.IsSettled);
        var still = view.AnimationTime;
        Assert.InRange(still, 9.5, 10.5);
        Assert.True(double.IsNaN(TgWelcomeWordmark.SweepPosition(still)));

        // An untouched welcome stays on its still frame.
        view.Tick(TimeSpan.FromSeconds(60));
        Assert.Equal(still, view.AnimationTime);

        // Input wakes it without jumping over the time it spent still.
        view.Wake();
        view.Tick(TimeSpan.FromSeconds(90));
        view.Tick(TimeSpan.FromSeconds(90.05));
        Assert.False(view.IsSettled);
        Assert.Equal(still + 0.05, view.AnimationTime, 6);
    }

    [Fact]
    public void Welcome_finishes_a_sweep_in_progress_before_settling()
    {
        using var view = new TgLandingView { Frame = new Rectangle(0, 0, 120, 40) };
        // A wake three seconds in moves the settle point into the next sweep (12 s to 14.5 s).
        for (var milliseconds = 0; milliseconds <= 3_000; milliseconds += 10)
            view.Tick(TimeSpan.FromMilliseconds(milliseconds));
        view.Wake();
        for (var milliseconds = 3_010; milliseconds <= 13_000; milliseconds += 10)
            view.Tick(TimeSpan.FromMilliseconds(milliseconds));
        Assert.False(view.IsSettled);
        for (var milliseconds = 13_010; milliseconds <= 16_000; milliseconds += 10)
            view.Tick(TimeSpan.FromMilliseconds(milliseconds));
        Assert.True(view.IsSettled);
        Assert.True(double.IsNaN(TgWelcomeWordmark.SweepPosition(view.AnimationTime)));
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
