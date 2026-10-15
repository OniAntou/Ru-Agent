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
    public async Task ExecuteSubmittedLine_DispatchesSlashCommandWithoutRendering()
    {
        var seen = new List<string>();
        var dispatcher = new AgentCommands.ChatLineDispatcher(
            handleSlash: line => { seen.Add("slash:" + line); return Task.FromResult(true); },
            handlePrompt: (line, _) => { seen.Add("prompt:" + line); return Task.CompletedTask; });

        Assert.True(await dispatcher.DispatchAsync("/status", CancellationToken.None));
        Assert.False(await dispatcher.DispatchAsync("sửa lỗi", CancellationToken.None));
        Assert.Equal(["slash:/status", "prompt:sửa lỗi"], seen);
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
