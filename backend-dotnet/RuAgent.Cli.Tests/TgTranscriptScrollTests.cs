using System.Drawing;
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The TG transcript used to be hard-pinned to its last page, so there was no way to read back
/// anything the terminal had already scrolled past. These pin the scrolling contract: which keys
/// scroll, that the bare arrows stay with the composer, and — the part that is easy to get wrong
/// — that a scrolled-back view stays parked while output keeps streaming below it.
/// </summary>
public sealed class TgTranscriptScrollTests
{
    [Fact]
    public void ScrollingFromLongUserRowsToShortProseClearsOldTextAndBackground()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Transcript.Append(new string('X', 100), TranscriptStyle.User);
        for (var i = 0; i < 60; i++) window.Transcript.Append($"short-{i}", TranscriptStyle.Prose);
        Run(app, window,
        [
            (100, w => w.Transcript.ScrollToTop()),
            (100, w => Assert.Contains("XXXXX", TgSnapshot.Capture(app)[w.Transcript.Frame.Y])),
            (100, w => w.Transcript.ScrollRows(1)),
            (100, w =>
            {
                var y = w.Transcript.Frame.Y;
                Assert.Contains("short-0", TgSnapshot.Capture(app)[y]);
                Assert.DoesNotContain("X", TgSnapshot.Capture(app)[y]);
                Assert.Equal(TgPalette.Attr(RuAgent.Cli.Tui.TuiToken.Ink).Background, TgSnapshot.CellAttribute(app, y, w.Transcript.Frame.X + 40).Background);
            })
        ]);
        app.Run(window);
    }

    private static List<string> Rows(string prefix, int count) =>
        Enumerable.Range(1, count).Select(i => $"{prefix}-{i:D3}").ToList();

    /// <summary>
    /// Runs the steps one per timeout tick and stops the app at the end. Assertions live in the
    /// steps, matching the landing tests, so a failure throws out of <c>app.Run</c> rather than
    /// being swallowed by the driver's loop.
    /// </summary>
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

    [Fact]
    public void Scrollback_PageUpGoesBack_CtrlHomeStopsAtTheTop_AndStaysThere()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();

        // The key loop is built by AgentCommands.Chat, not by Build(), so a test that skips it has
        // no key handler at all and every scroll assertion would pass for the wrong reason.
        using var loopStop = new CancellationTokenSource();
        var loop = new TgChatLoop(window, loopStop.Token);

        Run(
            app,
            window,
            [
                (150, w =>
                {
                    foreach (var row in Rows("dong", 400))
                    {
                        w.Transcript.Append(row, TranscriptStyle.Prose);
                    }

                    w.FocusComposer();
                }),
                (80, _ => app.Keyboard.RaiseKeyDownEvent(Key.PageUp)),
                (80, w =>
                {
                    Assert.False(w.Transcript.IsFollowingTail, "PageUp left the view pinned to the tail");
                    Assert.True(w.Transcript.ScrollPosition > 0);

                    // The hint row is the last row of the frame, not the first.
                    Assert.Contains("scrolled back", string.Join('\n', TgSnapshot.Capture(app)), StringComparison.Ordinal);
                }),
                (60, _ => app.Keyboard.RaiseKeyDownEvent(Key.Home.WithCtrl)),
                (80, w => Assert.Equal(0, w.Transcript.ScrollPosition)),
                (60, _ => app.Keyboard.RaiseKeyDownEvent(Key.PageUp)),
                (80, w => Assert.Equal(0, w.Transcript.ScrollPosition)),
            ]);

        app.Run(window);
    }

    [Fact]
    public void Scrollback_ParkedViewSurvivesNewOutput_AndCtrlEndResumesTheTail()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();

        // The key loop is built by AgentCommands.Chat, not by Build(), so a test that skips it has
        // no key handler at all and every scroll assertion would pass for the wrong reason.
        using var loopStop = new CancellationTokenSource();
        var loop = new TgChatLoop(window, loopStop.Token);
        var parked = 0;

        Run(
            app,
            window,
            [
                (150, w =>
                {
                    foreach (var row in Rows("dong", 400))
                    {
                        w.Transcript.Append(row, TranscriptStyle.Prose);
                    }

                    w.FocusComposer();
                }),
                (60, _ => app.Keyboard.RaiseKeyDownEvent(Key.PageUp)),
                (60, _ => app.Keyboard.RaiseKeyDownEvent(Key.PageUp)),
                (80, w =>
                {
                    parked = w.Transcript.ScrollPosition;
                    Assert.True(
                        parked > 0,
                        $"canScroll={w.Transcript.CanScroll} page={Math.Max(1, w.Transcript.Viewport.Height)} first={parked}");
                }),
                (60, w =>
                {
                    foreach (var row in Rows("them", 40))
                    {
                        w.Transcript.Append(row, TranscriptStyle.Prose);
                    }
                }),
                (80, w => Assert.Equal(parked, w.Transcript.ScrollPosition)),
                (60, _ => app.Keyboard.RaiseKeyDownEvent(Key.End.WithCtrl)),
                (80, w => Assert.True(w.Transcript.IsFollowingTail, "Ctrl+End did not resume the tail")),
            ]);

        app.Run(window);
    }

    [Fact]
    public void Scrollback_CtrlArrowScrollsOneRow_AndTheBareArrowStillWalksPromptHistory()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();

        // The key loop is built by AgentCommands.Chat, not by Build(), so a test that skips it has
        // no key handler at all and every scroll assertion would pass for the wrong reason.
        using var loopStop = new CancellationTokenSource();
        var loop = new TgChatLoop(window, loopStop.Token);
        var moved = 0;

        Run(
            app,
            window,
            [
                (150, w =>
                {
                    foreach (var row in Rows("dong", 400))
                    {
                        w.Transcript.Append(row, TranscriptStyle.Prose);
                    }

                    w.FocusComposer();
                }),
                (60, _ => app.Keyboard.RaiseKeyDownEvent(Key.CursorUp.WithCtrl)),
                (80, w => moved = w.Transcript.ScrollPosition),
                (60, _ => app.Keyboard.RaiseKeyDownEvent(Key.CursorDown.WithCtrl)),
                (80, w => Assert.Equal(moved + 1, w.Transcript.ScrollPosition)),
                (60, _ => app.Keyboard.RaiseKeyDownEvent(Key.CursorUp)),
                (80, w => Assert.Equal(moved + 1, w.Transcript.ScrollPosition)),
            ]);

        app.Run(window);
    }

    [Fact]
    public void Scrollback_DoesNothingOnATranscriptShorterThanTheViewport()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();

        // The key loop is built by AgentCommands.Chat, not by Build(), so a test that skips it has
        // no key handler at all and every scroll assertion would pass for the wrong reason.
        using var loopStop = new CancellationTokenSource();
        var loop = new TgChatLoop(window, loopStop.Token);

        Run(
            app,
            window,
            [
                (150, w =>
                {
                    w.Transcript.Append("dong-001", TranscriptStyle.Prose);
                    w.FocusComposer();
                }),
                (60, _ => app.Keyboard.RaiseKeyDownEvent(Key.PageUp)),
                (60, _ => app.Keyboard.RaiseKeyDownEvent(Key.Home.WithCtrl)),
                (80, w =>
                {
                    Assert.False(w.Transcript.CanScroll);
                    Assert.True(w.Transcript.IsFollowingTail);
                    Assert.DoesNotContain(
                        "scrolled back",
                        string.Join('\n', TgSnapshot.Capture(app)),
                        StringComparison.Ordinal);
                }),
            ]);

        app.Run(window);
    }
    [Fact]
    public void Scrollback_WheelScrollsTheTranscriptAndRaisesTheSameHint()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();

        // The key loop is built by AgentCommands.Chat, not by Build(), so a test that skips it
        // never updates the hint row and the scrollback marker would go unproved.
        using var loopStop = new CancellationTokenSource();
        var loop = new TgChatLoop(window, loopStop.Token);
        var afterUp = 0;
        var tailRow = 0;

        Run(
            app,
            window,
            [
                (150, w =>
                {
                    foreach (var row in Rows("dong", 400))
                    {
                        w.Transcript.Append(row, TranscriptStyle.Prose);
                    }

                    w.FocusComposer();
                }),
                (80, w =>
                {
                    w.Transcript.ScrollToTail();
                    tailRow = w.Transcript.ScrollPosition;
                }),
                (60, w => Wheel(w, MouseFlags.WheeledUp)),
                (100, w =>
                {
                    afterUp = w.Transcript.ScrollPosition;
                    Assert.True(
                        afterUp < tailRow,
                        $"wheel up did not scroll: first={afterUp} tail={tailRow}");
                    Assert.Contains(
                        "scrolled back",
                        string.Join('\n', TgSnapshot.Capture(app)),
                        StringComparison.Ordinal);
                }),
                (60, w => Wheel(w, MouseFlags.WheeledDown)),
                (100, w => Assert.True(w.Transcript.ScrollPosition > afterUp)),
            ]);

        app.Run(window);
    }

    /// <summary>
    /// Wheel notch over the transcript body. The position is read from the live frame so the test
    /// scrolls the transcript rather than whatever happens to sit under a guessed coordinate.
    /// </summary>
    private static void Wheel(TgChatWindow window, MouseFlags flags)
    {
        var transcript = window.Transcript;
        transcript.RaiseMouseEvent(
            new Mouse
            {
                Flags = flags,
                View = transcript,
                Position = new Point(2, transcript.Viewport.Height / 2),
            });
    }
}
