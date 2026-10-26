using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgChangesTests
{
    [Fact]
    public void Changes_list_and_preview_work_while_streaming_and_preserve_the_draft()
    {
        using var host = new ChatChangesWorkflowTests.Host { Live = true }; using var http = new HttpClient(host);
        var historyReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ActivitiesReady = historyReady.Task;
        using var api = new AgentApiClient(ChatChangesWorkflowTests.Host.Session, http);
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = 0; var cancelled = false; var handled = false; var step = 0;
        Exception? failure = null;
        loop.SetSubmissionSnapshotFactory(line => { snapshots++; return ChatSubmissionSnapshot.Create(line, "s", @"C:\workspace", "standard", "b", "m", [], []); });
        loop.SetPromptHandler(async (_, _, ct) => { try { await release.Task.WaitAsync(ct); } catch (OperationCanceledException) { cancelled = true; } });
        loop.SetChangesCommandHandler(async (argument, ct) =>
        {
            await new ChatChangesWorkflow(api).ExecuteAsync(ChatChangesWorkflowTests.Host.Owner, ChatChangesWorkflowTests.Host.Run, argument,
                (list, token) => TgPanelRequests.RequestChoiceAsync(window, list, token),
                (title, body, token) => TgPanelRequests.RequestDocumentAsync(window, title, body, token), new StringWriter(), false, ct);
            handled = true;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            try
            {
                if (step == 0)
                {
                    window.FocusComposer(); window.Composer.Input.Text = "active"; loop.Submit();
                    window.Composer.Input.Text = "/changes"; loop.Submit();
                    window.Composer.Input.Text = "unsent draft"; historyReady.TrySetResult(); step++; return true;
                }
                if (step == 1 && window.IsModalPanelOpen)
                {
                    // Ownership precedes the next ANSI paint; wait for the actual list frame.
                    var screen = string.Join('\n', TgSnapshot.Capture(app));
                    if (!screen.Contains("login.cs", StringComparison.Ordinal)) return true;
                    Assert.Contains("login.cs", screen); app.InjectKey(Key.Enter); step++; return true;
                }
                if (step == 2 && window.IsDocumentPanelOpen)
                {
                    var screen = string.Join('\n', TgSnapshot.Capture(app));
                    Assert.Contains("Before:", screen); Assert.Contains("old", screen); Assert.Contains("new", screen);
                    app.InjectKey(Key.Esc); step++; return true;
                }
                if (step == 3 && window.IsModalPanelOpen) { app.InjectKey(Key.Esc); step++; return true; }
                if (step == 4 && handled)
                {
                    Assert.Equal(1, snapshots); Assert.False(cancelled); Assert.Equal(0, window.Hints.QueuedCount);
                    Assert.Equal("unsent draft", window.Composer.Input.Text);
                    release.TrySetResult(); step++; loop.Stop(); app.RequestStop(); return false;
                }
            }
            catch (Exception ex) { failure = ex; release.TrySetResult(); loop.Stop(); app.RequestStop(); return false; }
            return true;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { release.TrySetResult(); loop.Stop(); app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(5, step);
    }

    [Fact]
    public void Preview_and_model_question_do_not_replace_each_other_or_share_answers()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        Task? preview = null; Task<string?>? question = null;
        var step = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (step == 0)
            {
                preview = TgPanelRequests.RequestDocumentAsync(window, "Recorded preview", ["Before: old", "After: new"], default);
                question = TgPanelRequests.RequestQuestionAsync(window, ChatInputWorkflowTests.Question, default);
                step++; return true;
            }
            if (step == 1 && window.IsDocumentPanelOpen)
            {
                Assert.False(question!.IsCompleted); Assert.False(window.IsModalPanelOpen);
                app.InjectKey(Key.Esc); step++; return true;
            }
            if (step == 2 && preview!.IsCompleted && window.IsModalPanelOpen)
            {
                Assert.False(question!.IsCompleted); Assert.False(window.IsDocumentPanelOpen);
                window.TryPasteText("manual answer"); app.InjectKey(Key.Enter); step++; return true;
            }
            if (step == 3 && question!.IsCompleted)
            { Assert.Equal("manual answer", question.Result); step++; app.RequestStop(); return false; }
            return true;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { app.RequestStop(); return false; });
        app.Run(window); Assert.Equal(4, step);
    }
}
