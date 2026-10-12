using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgIntegrationRegressionTests
{
    private static void RunSteps(IApplication app, Action[] steps, int index = 0)
    {
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (index == steps.Length) app.RequestStop();
            else { steps[index](); RunSteps(app, steps, index + 1); }
            return false;
        });
    }

    [Fact]
    public void BusyBannerSurvivesTypingBeforeAnyRequestIsAccepted()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        RunSteps(app, [
            () => { window.FocusComposer(); window.Hints.SetWorkspaceBusy("run_abcdef0123456789", "running"); },
            () => app.Keyboard.RaiseKeyDownEvent(Key.A),
            () => Assert.Contains("run_abcdef0123456789", string.Join('\n', TgSnapshot.Capture(app)), StringComparison.Ordinal),
        ]);
        app.Run(window);
        loop.Stop();
    }

    [Fact]
    public void ScrollbackHintRemainsVisibleWhileTheModelIsBusy()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        RunSteps(app, [
            () => { for (var i=0;i<80;i++) window.Transcript.Append($"line {i}", TranscriptStyle.Prose); window.FocusComposer(); window.Hints.SetBusy("spinner"); },
            () => app.Keyboard.RaiseKeyDownEvent(Key.PageUp),
            () => { Assert.False(window.Transcript.IsFollowingTail); Assert.Contains("scrolled back", string.Join('\n', TgSnapshot.Capture(app)), StringComparison.Ordinal); },
        ]);
        app.Run(window);
        loop.Stop();
    }

    [Fact]
    public void ClearResetsTheScrollbackHintAlongWithTheTranscript()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        RunSteps(app, [
            () => { for (var i=0;i<80;i++) window.Transcript.Append($"line {i}", TranscriptStyle.Prose); window.FocusComposer(); },
            () => app.Keyboard.RaiseKeyDownEvent(Key.PageUp),
            () => { Assert.False(window.Transcript.IsFollowingTail); window.Transcript.Clear(); },
            () => { Assert.True(window.Transcript.IsFollowingTail); Assert.DoesNotContain("scrolled back", string.Join('\n', TgSnapshot.Capture(app)), StringComparison.Ordinal); },
        ]);
        app.Run(window);
        loop.Stop();
    }
}
