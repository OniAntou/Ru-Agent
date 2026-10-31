using Xunit;
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;

namespace RuAgent.Cli.Tests;

public sealed class TgChatLoopTests
{
    [Fact]
    public async Task Stop_keeps_ui_completion_bounded_when_a_generic_handler_ignores_cancellation()
    {
        var enteredHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        loop.SetPromptHandler(async (_, _, _) =>
        {
            enteredHandler.TrySetResult();
            try { await releaseHandler.Task.ConfigureAwait(false); }
            finally { handlerFinished.TrySetResult(); }
        });

        window.Composer.Input.Text = "ignore cancellation";
        loop.Submit();
        await enteredHandler.Task.WaitAsync(TimeSpan.FromSeconds(5));
        loop.Stop();

        await loop.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var handlerWasStillRunning = !handlerFinished.Task.IsCompleted;
        releaseHandler.TrySetResult();
        await handlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(handlerWasStillRunning,
            "The UI wait remains bounded even when a generic handler ignores cancellation.");
    }

    [Fact]
    public void DocumentPagingDoesNotScrollTheConversationBehindIt()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            for (var i = 0; i < 100; i++) window.Transcript.Append($"chat {i}", TranscriptStyle.Prose);
            window.ShowDocument("activity", Enumerable.Range(0, 100).Select(i => $"tool {i}").ToArray());
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            app.InjectKey(Key.PageDown);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(450), () =>
        {
            Assert.True(window.Document.Offset > 0);
            Assert.True(window.Transcript.IsFollowingTail);
            loop.Stop();
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public async Task MultilinePromptIsStoredAsSeparateUserRows()
    {
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.SetPromptHandler((_, _, _) => blocker.Task);

        window.Composer.Input.Text = "line one\nline two";
        loop.Submit();

        var rows = window.Transcript.Lines
            .Where(row => row.Style == TranscriptStyle.User)
            .Select(row => row.Text)
            .ToList();
        Assert.Contains("  ❯ line one", rows);
        Assert.Contains("    line two", rows);
        Assert.DoesNotContain(rows, row => row.Contains('\n'));

        blocker.TrySetResult();
        loop.Stop();
        await loop.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CtrlHomeAndCtrlEnd_MoveBetweenTranscriptHistoryAndTail()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        _ = new TgChatLoop(window);
        for (var index = 0; index < 60; index++)
        {
            window.Transcript.Append($"history row {index:D2}", TranscriptStyle.Prose);
        }

        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            app.InjectKey(Key.Home.WithCtrl);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            Assert.False(window.Transcript.IsFollowingTail);
            Assert.Equal(0, window.Transcript.ScrollPosition);
            app.InjectKey(Key.End.WithCtrl);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(450), () =>
        {
            Assert.True(window.Transcript.IsFollowingTail);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
