using System.Drawing;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgWelcomeEffectsTests
{
    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 8)]
    [InlineData(2, 0)]
    public void Clock_respects_selected_frame_budget_without_changing_animation_speed(int selection, int expectedFrames)
    {
        var mode = (TgWelcomeEffects)selection;
        using var view = new TgLandingView { Frame = new Rectangle(0, 0, 120, 40), EffectsMode = mode };
        var changes = 0; var previous = view.AnimationTime;
        for (var milliseconds = 0; milliseconds <= 1000; milliseconds++)
        {
            view.Tick(TimeSpan.FromMilliseconds(milliseconds));
            if (view.AnimationTime != previous) changes++;
            previous = view.AnimationTime;
        }
        Assert.Equal(expectedFrames, changes);
        Assert.Equal(mode == TgWelcomeEffects.Off ? 0 : 1, view.AnimationTime, 6);
    }

    [Fact]
    public void Off_freezes_current_frame_and_resume_does_not_jump()
    {
        using var view = new TgLandingView { Frame = new Rectangle(0, 0, 120, 40) };
        view.Tick(TimeSpan.Zero); view.Tick(TimeSpan.FromMilliseconds(50));
        var frozen = view.AnimationTime; view.EffectsMode = TgWelcomeEffects.Off;
        for (var seconds = 1; seconds < 60; seconds++) view.Tick(TimeSpan.FromSeconds(seconds));
        Assert.Equal(frozen, view.AnimationTime);
        view.EffectsMode = TgWelcomeEffects.Low;
        view.Tick(TimeSpan.FromSeconds(100)); Assert.Equal(frozen, view.AnimationTime);
        view.Tick(TimeSpan.FromSeconds(100.125)); Assert.Equal(frozen + .125, view.AnimationTime, 6);
    }

    [Fact]
    public void Static_sky_reuses_exact_frame_but_resize_and_new_time_render_again()
    {
        var sky = new TuiGalaxySky(); sky.Render(120, 40, 7);
        var first = sky.Dots.ToArray(); var colors = sky.Colors.ToArray();
        for (var repaint = 0; repaint < 100; repaint++) sky.Render(120, 40, 7);
        Assert.Equal(1, sky.RenderCount); Assert.Equal(first, sky.Dots.ToArray()); Assert.Equal(colors, sky.Colors.ToArray());
        sky.Render(80, 24, 7); Assert.Equal(2, sky.RenderCount); Assert.Equal(80 * 24, sky.Dots.Length);
        sky.Render(80, 24, 7.5); Assert.Equal(3, sky.RenderCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_mouse_and_tab_enter_cycle_modes_keep_draft_and_hide_control_in_chat(bool mouse)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window); var submissions = 0;
        loop.SetPromptHandler((_, _, _) => { submissions++; return Task.CompletedTask; });
        var stage = 0; Exception? failure = null; double frozen = 0; long renders = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                if (stage < 3)
                {
                    Assert.True(window.EffectsButton.Visible);
                    Assert.True(window.EffectsButton.Viewport.Height > 0, $"fx {window.EffectsButton.Frame}/{window.EffectsButton.Viewport}; shadow={window.EffectsButton.ShadowStyle}; margin={window.EffectsButton.Margin!.Thickness}; border={window.EffectsButton.Border!.Thickness}; pad={window.EffectsButton.Padding!.Thickness}");
                    if (stage == 0) { window.FocusComposer(); window.Composer.Input.Text = "keep this draft"; }
                    if (mouse) app.Mouse.RaiseMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonClicked,
                        ScreenPosition = window.EffectsButton.ViewportToScreen(new Rectangle(3, 0, 1, 1)).Location });
                    else { window.FocusComposer(); app.InjectKey(Key.Tab); Assert.Same(window.EffectsButton, window.MostFocused); app.InjectKey(Key.Enter); }
                    Assert.Equal((TgWelcomeEffects)((stage + 1) % 3), window.Landing.EffectsMode);
                    Assert.Equal("keep this draft", window.Composer.Input.Text);
                    Assert.Same(window.Composer.Input, window.MostFocused);
                    Assert.False(window.Transcript.HasContent);
                    if (stage == 1) { frozen = window.Landing.AnimationTime; renders = window.Landing.SkyRenderCount; }
                    stage++; return true;
                }
                if (stage == 3)
                {
                    window.Transcript.Append("chat content", TranscriptStyle.Prose);
                    Assert.False(window.EffectsButton.Visible); stage++; return true;
                }
                if (stage == 4)
                {
                    window.ResetTranscript(); Assert.True(window.EffectsButton.Visible);
                    window.EffectsButton.SetFocus(); app.InjectKey(Key.Enter); // On -> Low
                    window.EffectsButton.SetFocus(); app.InjectKey(Key.Enter); // Low -> Off
                    frozen = window.Landing.AnimationTime;
                    stage++; return true;
                }
                if (stage == 5)
                {
                    renders = window.Landing.SkyRenderCount;
                    for (var tick = 0; tick < 100; tick++) window.TickWelcome();
                    window.Landing.SetNeedsDraw(); stage++; return true;
                }
                Assert.Equal(frozen, window.Landing.AnimationTime);
                Assert.Equal(renders, window.Landing.SkyRenderCount);
                Assert.Contains(string.Join('\n', TgSnapshot.Capture(app)), value => value is >= '\u2801' and <= '\u28ff');
                var screen = string.Join('\n', TgSnapshot.Capture(app));
                Assert.True(screen.Contains("FX: Off"), $"button {window.EffectsButton.Frame}; viewport {window.EffectsButton.Viewport}; {screen}");
                Assert.Equal(0, submissions);
                stage++; loop.Stop(); app.RequestStop(); return false;
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
        });
        app.AddTimeout(TimeSpan.FromSeconds(8), () => { app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(7, stage);
    }

    [Fact]
    public void Effects_choice_follows_session_pages_in_one_application()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var shell = new TgSessionHostWindow();
        var first = TgChatWindow.Build(); var second = TgChatWindow.Build();
        shell.AddPage(first); shell.AddPage(second); shell.Activate(first);
        var stage = 0; Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () => {
            try {
                if (stage == 0) {
                    first.EffectsButton.SetFocus(); app.InjectKey(Key.Enter);
                    Assert.Equal(TgWelcomeEffects.Low, first.Landing.EffectsMode);
                    shell.Activate(second); stage++; return true;
                }
                Assert.Equal(TgWelcomeEffects.Low, second.Landing.EffectsMode);
                Assert.Equal("FX: Low", second.EffectsButton.Text);
                second.EffectsButton.SetFocus(); app.InjectKey(Key.Enter);
                shell.Activate(first); Assert.Equal(TgWelcomeEffects.Off, first.Landing.EffectsMode);
                stage++; app.RequestStop(); return false;
            } catch (Exception error) { failure = error; app.RequestStop(); return false; }
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { app.RequestStop(); return false; });
        app.Run(shell);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(2, stage);
    }
}
