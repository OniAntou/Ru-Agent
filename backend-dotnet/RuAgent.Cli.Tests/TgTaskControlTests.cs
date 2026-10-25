using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgTaskControlTests
{
    [Fact]
    public void Tasks_can_stop_a_process_while_model_streams_without_claiming_attachments_or_cancelling_the_turn()
    {
        using var host = new ChatTaskWorkflowTests.Host(); using var http = new HttpClient(host);
        using var api = new AgentApiClient(ChatTaskWorkflowTests.Session, http);
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnCancelled = false;
        var snapshots = 0;
        var handled = false;
        loop.SetSubmissionSnapshotFactory(line => { snapshots++; return ChatSubmissionSnapshot.Create(line, "s", @"C:\workspace", "standard", "b", "m", [], []); });
        loop.SetPromptHandler(async (_, _, ct) =>
        {
            try { await release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { turnCancelled = true; }
        });
        loop.SetTaskCommandHandler(async (argument, ct) =>
        {
            await new ChatTaskWorkflow(api).ExecuteAsync(ChatTaskWorkflowTests.SessionId, ChatTaskWorkflowTests.RunId, argument,
                (list, token) => TgPanelRequests.RequestChoiceAsync(window, list, token), new StringWriter(), false, ct);
            handled = true;
        });
        var step = 0;
        Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(80), () =>
        {
            try
            {
                if (step == 0)
                {
                    window.FocusComposer(); window.Composer.Input.Text = "active"; loop.Submit();
                    window.Composer.Input.Text = "/tasks kill " + ChatTaskWorkflowTests.ExecutionId; loop.Submit();
                    step++; return true;
                }
                if (step == 1 && window.IsModalPanelOpen)
                {
                    var screen = string.Join('\n', TgSnapshot.Capture(app));
                    Assert.Contains("Stop background task?", screen);
                    Assert.Contains("Keep running", screen);
                    app.InjectKey(Key.CursorDown); app.InjectKey(Key.Enter);
                    step++; return true;
                }
                if (step == 2 && handled)
                {
                    Assert.Equal(1, snapshots);
                    Assert.Equal(0, window.Hints.QueuedCount);
                    Assert.False(turnCancelled);
                    Assert.Single(host.Commands, command => command.StartsWith("/tasks kill"));
                    release.TrySetResult();
                    step++; loop.Stop(); app.RequestStop(); return false;
                }
            }
            catch (Exception ex) { failure = ex; release.TrySetResult(); loop.Stop(); app.RequestStop(); return false; }
            return true;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { release.TrySetResult(); loop.Stop(); app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(3, step);
    }

    [Fact]
    public async Task Escape_during_task_fetch_cancels_only_task_control_and_stop_drains_its_lifetime()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskCancelled = false; var turnCancelled = false;
        loop.SetPromptHandler(async (_, _, ct) =>
        { try { await release.Task.WaitAsync(ct); } catch (OperationCanceledException) { turnCancelled = true; } });
        loop.SetTaskCommandHandler(async (_, ct) =>
        { try { await Task.Delay(System.Threading.Timeout.Infinite, ct); } catch (OperationCanceledException) { taskCancelled = true; } });
        var checkedState = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.Composer.Input.Text = "active"; loop.Submit();
            window.Composer.Input.Text = "/tasks"; loop.Submit();
            loop.Cancel();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(350), () =>
        {
            Assert.True(taskCancelled); Assert.False(turnCancelled);
            checkedState = true; release.TrySetResult(); loop.Stop(); app.RequestStop(); return false;
        });
        app.Run(window);
        Assert.True(checkedState);
        await loop.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Task_choice_and_model_question_are_serialized_and_cannot_consume_each_others_answer()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        Task<string?>? first = null; Task<string?>? second = null;
        var stage = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (stage == 0)
            {
                first = TgPanelRequests.RequestChoiceAsync(window, new ChoiceList("Background tasks", [new ChoiceItem("Refresh", "", "refresh")]), default);
                second = TgPanelRequests.RequestQuestionAsync(window, ChatInputWorkflowTests.Question, default);
                stage++; return true;
            }
            if (stage == 1 && window.IsModalPanelOpen)
            {
                Assert.False(second!.IsCompleted);
                app.InjectKey(Key.Enter); stage++; return true;
            }
            if (stage == 2 && window.IsModalPanelOpen && first!.IsCompleted)
            {
                Assert.Equal("refresh", first.Result);
                Assert.False(second!.IsCompleted);
                window.TryPasteText("manual answer"); app.InjectKey(Key.Enter); stage++; return true;
            }
            if (stage == 3 && second!.IsCompleted)
            {
                Assert.Equal("manual answer", second.Result);
                stage++; app.RequestStop(); return false;
            }
            return true;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { app.RequestStop(); return false; });
        app.Run(window);
        Assert.Equal(4, stage);
    }
}
