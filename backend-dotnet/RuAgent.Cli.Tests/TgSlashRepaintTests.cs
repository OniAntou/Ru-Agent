using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgSlashRepaintTests
{
    private sealed record InputStep(Key Key, string ExpectedText, bool ExpectNoLandingPaint);

    [Fact]
    public void ClosedEditsAndStableSlashMatchesDoNotRepaintLanding_AndMenuGeometryAndEraseRemainCorrect()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var columns = 0;
        var rows = 0;
        var steps = new[]
        {
            new InputStep(new Key('p'), "p", true),
            new InputStep(new Key('l'), "pl", true),
            new InputStep(Key.Backspace, "p", true),
            new InputStep(Key.Backspace, "", true),
            new InputStep(new Key('/'), "/", false),
            new InputStep(new Key('m'), "/m", false),
            new InputStep(new Key('o'), "/mo", true),
            new InputStep(new Key('d'), "/mod", true),
            new InputStep(new Key('e'), "/mode", true),
            new InputStep(new Key('l'), "/model", true),
            new InputStep(Key.Backspace, "/mode", true),
            new InputStep(Key.Backspace, "/mod", true),
            new InputStep(Key.Backspace, "/mo", true),
            new InputStep(Key.Backspace, "/m", true),
            new InputStep(Key.Backspace, "/", false),
            new InputStep(Key.Backspace, "", false),
            new InputStep(new Key('p'), "p", true),
            new InputStep(new Key('l'), "pl", true),
        };

        var drawCompletions = 0;
        var landingClears = 0;
        var composerClears = 0;
        app.LayoutAndDrawComplete += (_, _) => drawCompletions++;
        window.Landing.ClearingViewport += (_, _) => landingClears++;
        window.Composer.Input.ClearingViewport += (_, _) => composerClears++;

        var initialized = false;
        var initialFrame = 0;
        var initialPaint = 0;
        var stepIndex = 0;
        var pending = false;
        var framesBeforeInput = 0;
        var composerPaintBeforeInput = 0;
        var landingPaintBeforeInput = 0;
        var menuOpenHeight = 0;
        var dirtyBeforeInput = string.Empty;
        var resizeCount = 0;
        Exception? failure = null;

        app.AddTimeout(TimeSpan.FromMilliseconds(40), () =>
        {
            try
            {
                if (app.AnsiStartupGate is { IsReady: false }) return true;
                if (app.Driver!.Cols < 60 || app.Driver.Rows < 20) return true;
                if (app.Driver.Cols != columns || app.Driver.Rows != rows)
                {
                    resizeCount++;
                    // Use the driver's observed size: forcing a different size
                    // fights its periodic native console-size polling.
                    columns = app.Driver.Cols;
                    rows = app.Driver.Rows;
                    if (initialized)
                    {
                        // ANSI startup can report another size after the first frame.
                        // A real resize legitimately redraws the background: restart
                        // the input scenario, then measure only at the pinned size.
                        window.Composer.Input.Text = string.Empty;
                        window.RefreshSessionLayout();
                        window.FocusComposer();
                        stepIndex = 0;
                        pending = false;
                        initialFrame = drawCompletions;
                        initialPaint = composerClears;
                    }
                    return true;
                }

                if (!initialized)
                {
                    window.SetHostedApplication(app);
                    loop.SetViewActive(app);
                    window.RefreshSessionLayout();
                    window.FocusComposer();
                    initialFrame = drawCompletions;
                    initialPaint = composerClears;
                    initialized = true;
                    return true;
                }

                if (drawCompletions <= initialFrame || composerClears <= initialPaint) return true;

                if (pending)
                {
                    if (drawCompletions <= framesBeforeInput || composerClears <= composerPaintBeforeInput)
                        return true;

                    var step = steps[stepIndex];
                    Assert.Equal(step.ExpectedText, window.Composer.Input.Text?.ToString() ?? string.Empty);
                    if (step.ExpectNoLandingPaint)
                        Assert.True(landingPaintBeforeInput == landingClears,
                            $"Step {stepIndex} ({step.ExpectedText}): landing paints {landingPaintBeforeInput} -> {landingClears}; before={dirtyBeforeInput}; resizes={resizeCount}; root draw={window.NeedsDraw}, layout={window.NeedsLayout}");

                    if (step.ExpectedText.StartsWith("/", StringComparison.Ordinal))
                    {
                        var dropdown = Assert.Single(window.SubViews.OfType<TgSlashDropdown>());
                        Assert.True(window.IsSlashDropdownOpen);
                        Assert.Equal(window.Composer.Frame.Y, dropdown.Frame.Bottom);
                        Assert.Equal(window.Composer.Frame.X, dropdown.Frame.X);
                        Assert.Equal(window.Composer.Frame.Width, dropdown.Frame.Width);
                        Assert.Equal(Math.Min(dropdown.PanelHeight, Math.Max(3, window.Composer.Frame.Y)), dropdown.Frame.Height);
                        var screen = TgSnapshot.Capture(app);
                        var footerRow = window.Frame.Y + dropdown.Frame.Bottom - 1;
                        Assert.InRange(footerRow, 0, screen.Length - 1);
                        Assert.Contains("esc close", screen[footerRow]);

                        if (step.ExpectedText == "/")
                        {
                            menuOpenHeight = dropdown.Frame.Height;
                            Assert.Equal(TgSlashDropdown.MaxVisible + TuiCard.TuiCardFrameRows, menuOpenHeight);
                            Assert.Contains("/provider", string.Join('\n', screen));
                        }
                        else if (step.ExpectedText == "/m")
                        {
                            Assert.True(dropdown.Frame.Height < menuOpenHeight);
                            Assert.Equal(1 + TuiCard.TuiCardFrameRows, dropdown.Frame.Height);
                            Assert.DoesNotContain("/provider", string.Join('\n', screen));
                        }
                    }
                    else
                    {
                        Assert.False(window.IsSlashDropdownOpen);
                        if (step.ExpectedText.Length == 0)
                            Assert.DoesNotContain("/model", string.Join('\n', TgSnapshot.Capture(app)));
                    }

                    pending = false;
                    stepIndex++;
                }

                if (stepIndex == steps.Length)
                {
                    app.RequestStop();
                    return false;
                }

                var next = steps[stepIndex];
                framesBeforeInput = drawCompletions;
                composerPaintBeforeInput = composerClears;
                landingPaintBeforeInput = landingClears;
                dirtyBeforeInput = $"root draw={window.NeedsDraw}, layout={window.NeedsLayout}";
                app.InjectKey(next.Key);
                pending = true;
                return true;
            }
            catch (Exception error)
            {
                failure = error;
                app.RequestStop();
                return false;
            }
        });
        app.AddTimeout(TimeSpan.FromSeconds(8), () =>
        {
            failure ??= new TimeoutException($"TUI repaint scenario did not settle: step={stepIndex}, pending={pending}, resizes={resizeCount}, frames={drawCompletions}/{initialFrame}/{framesBeforeInput}, inputPaints={composerClears}/{initialPaint}/{composerPaintBeforeInput}, text={window.Composer.Input.Text}");
            app.RequestStop();
            return false;
        });

        app.Run(window);
        loop.SetViewActive(null);
        window.SetHostedApplication(null);
        loop.Stop();

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(steps.Length, stepIndex);
    }

    [Fact]
    public void SettingIdenticalMatchesPreservesSelectionAndLeavesDropdownClean()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        const int columns = 100;
        const int rows = 30;

        var drawCompletions = 0;
        app.LayoutAndDrawComplete += (_, _) => drawCompletions++;
        var phase = 0;
        var framesBefore = 0;
        Exception? failure = null;

        app.AddTimeout(TimeSpan.FromMilliseconds(40), () =>
        {
            try
            {
                if (app.AnsiStartupGate is { IsReady: false }) return true;
                if (app.Driver!.Cols != columns || app.Driver.Rows != rows)
                {
                    app.Driver.SetScreenSize(columns, rows);
                    return true;
                }

                if (phase == 0)
                {
                    window.SetHostedApplication(app);
                    loop.SetViewActive(app);
                    window.RefreshSessionLayout();
                    window.FocusComposer();
                    phase = 1;
                    return true;
                }

                if (phase == 1)
                {
                    window.Composer.Input.Text = "/";
                    framesBefore = drawCompletions;
                    phase = 2;
                    return true;
                }

                if (phase == 2)
                {
                    if (drawCompletions <= framesBefore) return true;
                    window.MoveSlashDropdown(1);
                    framesBefore = drawCompletions;
                    phase = 3;
                    return true;
                }

                if (phase == 3)
                {
                    if (drawCompletions <= framesBefore) return true;
                    var dropdown = Assert.Single(window.SubViews.OfType<TgSlashDropdown>());
                    Assert.Equal("/provider", dropdown.SelectedCommand);
                    Assert.False(dropdown.NeedsDraw);
                    Assert.False(dropdown.NeedsLayout);

                    framesBefore = drawCompletions;
                    dropdown.SetMatches(ChatCommandCatalog.FindMatches("/"));
                    Assert.Equal("/provider", dropdown.SelectedCommand);
                    Assert.False(dropdown.NeedsDraw);
                    Assert.False(dropdown.NeedsLayout);
                    phase = 4;
                    return true;
                }

                Assert.Equal(framesBefore, drawCompletions);
                app.RequestStop();
                return false;
            }
            catch (Exception error)
            {
                failure = error;
                app.RequestStop();
                return false;
            }
        });
        app.AddTimeout(TimeSpan.FromSeconds(6), () =>
        {
            failure ??= new TimeoutException("Identical-match scenario did not settle.");
            app.RequestStop();
            return false;
        });

        app.Run(window);
        loop.SetViewActive(null);
        window.SetHostedApplication(null);
        loop.Stop();

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(4, phase);
    }
}
