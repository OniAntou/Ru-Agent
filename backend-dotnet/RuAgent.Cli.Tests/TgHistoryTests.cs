using RuAgent.Client;
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgHistoryTests
{
    [Fact]
    public void Branch_creation_refuses_typeahead_without_claiming_the_second_draft()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build(); var loop = new TgChatLoop(window);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = 0; var checkedGuard = false;
        loop.SetHistoryStateProviders(() => true, _ => false);
        loop.SetSubmissionSnapshotFactory(text => { snapshots++; return ChatSubmissionSnapshot.Create(text, "source", "w", "standard", "b", "m", [], []); });
        loop.SetPromptHandler(async (_, _, ct) => await release.Task.WaitAsync(ct));
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            window.Composer.Input.Text = "first"; loop.Submit();
            window.Composer.Input.Text = "keep second draft"; loop.Submit();
            checkedGuard = snapshots == 1 && window.Composer.Input.Text == "keep second draft" && window.Hints.FeedbackIsError;
            release.TrySetResult(); loop.Stop(); app.RequestStop(); return false;
        });
        app.Run(window); Assert.True(checkedGuard);
    }

    [Fact]
    public void Explicit_history_reload_can_leave_paused_state_without_replaying_a_prompt()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build(); var loop = new TgChatLoop(window);
        var snapshots = 0; var calls = 0; var step = 0; Exception? failure = null;
        loop.SetHistoryStateProviders(() => false, line => line == "/sessions id:source");
        loop.SetSubmissionSnapshotFactory(text => { snapshots++; return ChatSubmissionSnapshot.Create(text, "source", "w", "standard", "b", "m", [], []); });
        loop.SetPromptHandler((line, _, _) => { calls++; if (line.StartsWith("/sessions")) loop.FinishHistoryReload(); return Task.CompletedTask; });
        loop.PauseQueuedSubmissions();
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            try
            {
                if (step == 0) { window.Composer.Input.Text = "/sessions id:source"; loop.Submit(); step++; return true; }
                if (calls == 1 && step == 1 && window.Hints.ActiveState == TgHintsState.Idle)
                {
                    Assert.Equal(1, snapshots); Assert.Equal(0, window.Hints.QueuedCount);
                    window.Composer.Input.Text = "explicit new send"; loop.Submit(); step++; return true;
                }
                if (calls == 2) { Assert.Equal(2, snapshots); step = 3; loop.Stop(); app.RequestStop(); return false; }
            }
            catch (Exception ex) { failure = ex; loop.Stop(); app.RequestStop(); return false; }
            return true;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { failure = new Exception("Recovery timed out at " + step); loop.Stop(); app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(3, step);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_history_stages_edit_without_forking_until_send_or_cancels(bool cancel)
    {
        using var host = new ChatHistoryWorkflowTests.Host(); using var http = new HttpClient(host);
        using var agents = new AgentApiClient(ChatHistoryWorkflowTests.Host.Session, http);
        var workflow = new ChatHistoryWorkflow(new SessionApiClient(ChatHistoryWorkflowTests.Host.Session, http), agents);
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build(); var loop = new TgChatLoop(window);
        ChatHistoryBranch? plan = null; var snapshots = 0; var stage = 0; var handled = false; var sent = false; Exception? failure = null;
        loop.SetSubmissionSnapshotFactory(text => { snapshots++; return ChatSubmissionSnapshot.Create(text, "source", "w", "standard", "b", "m", [], []); });
        loop.SetRewindCommandHandler(async (argument, ct) =>
        {
            plan = await workflow.SelectAsync("source", argument, () => true,
                (list, token) => TgPanelRequests.RequestChoiceAsync(window, list, token), null, new StringWriter(), false, ct);
            app.Invoke(() => { if (plan is not null) window.Composer.Input.Text = plan.Draft!; handled = true; });
        });
        loop.SetPromptHandler(async (_, _, ct) => { await workflow.CommitAsync(plan!, ct); sent = true; });
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            try
            {
                if (stage == 0) { window.Composer.Input.Text = "/history"; loop.Submit(); stage++; return true; }
                if (stage == 1 && window.IsModalPanelOpen)
                {
                    if (cancel) app.InjectKey(Key.Esc);
                    else { app.InjectKey(Key.CursorDown); app.InjectKey(Key.Enter); }
                    stage++; return true;
                }
                if (!cancel && stage == 2 && window.IsModalPanelOpen
                    && string.Join('\n', TgSnapshot.Capture(app)).Contains("View turn", StringComparison.Ordinal))
                { app.InjectKey(Key.CursorDown); app.InjectKey(Key.Enter); stage++; return true; }
                if (handled && !cancel && stage == 3)
                {
                    Assert.Equal("first\nrequest", window.Composer.Input.Text); Assert.Equal(0, snapshots); Assert.Equal(0, host.Forks);
                    window.Composer.Input.Text = "revised request"; loop.Submit(); stage++; return true;
                }
                if ((cancel && handled) || sent)
                {
                    Assert.Equal(cancel ? 0 : 1, host.Forks); Assert.Equal(cancel ? 0 : 1, snapshots);
                    Assert.Equal(4, host.Messages.GetArrayLength()); stage = 5; loop.Stop(); app.RequestStop(); return false;
                }
            }
            catch (Exception ex) { failure = ex; loop.Stop(); app.RequestStop(); return false; }
            return true;
        });
        app.AddTimeout(TimeSpan.FromSeconds(8), () => { failure = new Exception("Timed out at stage " + stage); loop.Stop(); app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(5, stage);
    }
}
