using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgHintsViewTests
{
    [Fact]
    public void FeedbackDoesNotChangeTheActualRunState_AndIdleClearsIt()
    {
        var hints = new TgHintsView();
        hints.SetIdle("D:/LLM");

        hints.ShowFeedback("submission too large", isError: true);

        Assert.Equal(TgHintsState.Idle, hints.ActiveState);
        Assert.Equal("submission too large", hints.FeedbackMessage);
        Assert.True(hints.FeedbackIsError);

        hints.SetRunning(2);
        Assert.Equal(TgHintsState.Running, hints.ActiveState);
        Assert.Equal(2, hints.QueuedCount);
        Assert.Null(hints.FeedbackMessage);

        hints.ShowFeedback("queued prompt refused", isError: true);
        Assert.Equal(TgHintsState.Running, hints.ActiveState);
        Assert.Equal(2, hints.QueuedCount);

        hints.SetIdle("D:/LLM");
        Assert.Equal(TgHintsState.Idle, hints.ActiveState);
        Assert.Equal(0, hints.QueuedCount);
        Assert.Null(hints.FeedbackMessage);
        Assert.False(hints.FeedbackIsError);
    }

    [Theory]
    [InlineData(TgHintsState.WaitingForApproval)]
    [InlineData(TgHintsState.Paused)]
    public void QueueCountRemainsVisibleInNonRunningStates(TgHintsState state)
    {
        var hints = new TgHintsView();
        if (state == TgHintsState.WaitingForApproval)
        {
            hints.SetRunning(3);
            hints.SetWaitingForApproval(true);
        }
        else
        {
            hints.SetPaused(3);
        }

        Assert.Equal(state, hints.ActiveState);
        Assert.Equal(3, hints.QueuedCount);
        Assert.False(hints.IsRunning);
    }

    [Fact]
    public void IdleQueueIsPresentedAsWaitingWorkRatherThanReady()
    {
        var hints = new TgHintsView();
        hints.SetIdle("D:/LLM");
        hints.SetQueueCount(2);

        Assert.Equal(TgHintsState.Idle, hints.ActiveState);
        Assert.Contains("2 queued", hints.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Ready", hints.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void IdleAttachmentLabelKeepsPendingCountAndWorkspace()
    {
        var hints = new TgHintsView();

        hints.SetIdle("[2 attachments] C:/Users/USER");

        Assert.Equal("[2 attachments] C:/Users/USER", hints.StatusText);
    }

    [Fact]
    public void ResolvingApprovalRestoresRunStateAndQueue()
    {
        var hints = new TgHintsView();
        hints.SetRunning(2);
        hints.SetWaitingForApproval(true);
        hints.SetWaitingForApproval(false);

        Assert.Equal(TgHintsState.Running, hints.ActiveState);
        Assert.Equal(2, hints.QueuedCount);
    }

    [Fact]
    public void FooterAtEightyColumnsKeepsPausedStateAndQueueVisible()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var hints = new TgHintsView { X = 0, Y = 0, Width = Dim.Fill() };
        hints.SetPaused(3);
        window.Add(hints);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var row = TgSnapshot.Capture(app)[0];
            Assert.Contains("Paused", row, StringComparison.Ordinal);
            Assert.Contains("3 queued", row, StringComparison.Ordinal);
            Assert.Contains("ctrl+p commands", row, StringComparison.Ordinal);
            Assert.DoesNotContain("▄", row, StringComparison.Ordinal);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void RunningQueueFeedbackKeepsCancelHintAndRefusalVisibleAtEightyColumns()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var hints = new TgHintsView { X = 0, Y = 0, Width = Dim.Fill() };
        hints.SetRunning(3);
        hints.ShowFeedback("queue full · enter retries", isError: true);
        window.Add(hints);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var row = TgSnapshot.Capture(app)[0];
            Assert.Contains("3 queued", row, StringComparison.Ordinal);
            Assert.Contains("queue full · enter retries", row, StringComparison.Ordinal);
            Assert.Contains("esc interrupt", row, StringComparison.Ordinal);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void FooterHintsScaleWithWidthAndKeepCommandsVisibleInCompactMode()
    {
        var compact = TgStatusPresentation.KeyHints(80, compact: true);
        var narrow = TgStatusPresentation.KeyHints(45, compact: false);
        var standard = TgStatusPresentation.KeyHints(80, compact: false);
        var wide = TgStatusPresentation.KeyHints(160, compact: false);

        Assert.Contains("ctrl+p commands", compact);
        Assert.Contains("ctrl+p commands", narrow);
        Assert.DoesNotContain("enter send", standard);
        Assert.DoesNotContain("ctrl+j newline", standard);
        Assert.Contains("ctrl+p commands", standard);
        Assert.DoesNotContain("ctrl+o details", standard);
        Assert.DoesNotContain("ctrl+shift+c copy", standard);
        Assert.DoesNotContain("ctrl+o details", wide);
        Assert.DoesNotContain("ctrl+shift+c copy", wide);
    }
}
