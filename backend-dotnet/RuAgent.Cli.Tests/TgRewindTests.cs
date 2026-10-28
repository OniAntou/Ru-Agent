using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgRewindTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Busy_or_paused_queue_refuses_rewind_before_claiming_attachments(bool paused)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build(); var loop = new TgChatLoop(window);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = 0; var rewinds = 0; var step = 0; Exception? failure = null;
        loop.SetSubmissionSnapshotFactory(line => { snapshots++; return ChatSubmissionSnapshot.Create(line, "s", @"C:\workspace", "standard", "b", "m", [], []); });
        loop.SetPromptHandler(async (_, _, ct) => await release.Task.WaitAsync(ct));
        loop.SetRewindCommandHandler((_, _) => { rewinds++; return Task.CompletedTask; });
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            try
            {
                if (step == 0)
                {
                    window.Composer.Input.Text = "active"; loop.Submit();
                    if (paused) { window.Composer.Input.Text = "queued"; loop.Submit(); loop.PauseQueuedSubmissions(); release.TrySetResult(); }
                    step++; return true;
                }
                if (paused && window.Hints.ActiveState != TgHintsState.Paused) return true;
                window.Composer.Input.Text = "/rewind"; loop.Submit();
                Assert.Equal("/rewind", window.Composer.Input.Text); Assert.Equal(0, rewinds);
                Assert.Equal(paused ? 2 : 1, snapshots); Assert.True(window.Hints.FeedbackIsError);
                Assert.Equal(paused ? 1 : 0, window.Hints.QueuedCount); step++;
            }
            catch (Exception ex) { failure = ex; }
            release.TrySetResult(); loop.Stop(); app.RequestStop(); return false;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { release.TrySetResult(); loop.Stop(); app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(2, step);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_picker_confirms_exact_raw_history_prefix_or_cancels(bool cancel)
    {
        using var host = new ChatRewindWorkflowTests.Host(); using var http = new HttpClient(host);
        var sessions = new SessionApiClient(ChatRewindWorkflowTests.Host.Session, http);
        using var agents = new AgentApiClient(ChatRewindWorkflowTests.Host.Session, http);
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build(); var loop = new TgChatLoop(window);
        var handled = false; var snapshots = 0; var step = 0; Exception? failure = null;
        loop.SetSubmissionSnapshotFactory(_ => { snapshots++; return null; });
        loop.SetRewindCommandHandler(async (argument, ct) =>
        {
            await new ChatRewindWorkflow(sessions, agents).ExecuteAsync(ChatRewindWorkflowTests.Host.Owner, argument, () => true,
                (list, token) => TgPanelRequests.RequestChoiceAsync(window, list, token), new StringWriter(), false, ct);
            handled = true;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            try
            {
                if (step == 0) { window.FocusComposer(); window.Composer.Input.Text = "/rewind"; loop.Submit(); step++; return true; }
                if (step == 1 && window.IsModalPanelOpen)
                {
                    // Targets are newest first: #5, #4, #3, #1 (hidden #2 still counts).
                    if (cancel) app.InjectKey(Key.Esc);
                    else { app.InjectKey(Key.CursorDown); app.InjectKey(Key.CursorDown); app.InjectKey(Key.Enter); }
                    step++; return true;
                }
                if (!cancel && step == 2 && window.IsModalPanelOpen)
                {
                    // Modal ownership changes before the next frame is painted.
                    if (!string.Join('\n', TgSnapshot.Capture(app)).Contains("Keep history", StringComparison.Ordinal)) return true;
                    Assert.Equal(0, host.Posts);
                    app.InjectKey(Key.CursorDown); app.InjectKey(Key.Enter); step++; return true;
                }
                if (handled)
                {
                    Assert.Equal(0, snapshots); Assert.Equal(cancel ? 0 : 1, host.Posts);
                    if (!cancel) { Assert.Equal(2, host.PostedIndex); Assert.Equal(3, host.Messages.Length); }
                    step = 4; loop.Stop(); app.RequestStop(); return false;
                }
            }
            catch (Exception ex) { failure = ex; loop.Stop(); app.RequestStop(); return false; }
            return true;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { failure = new Exception("Timed out at step " + step + "\n" + string.Join('\n', TgSnapshot.Capture(app))); loop.Stop(); app.RequestStop(); return false; });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(4, step);
    }

    [Fact]
    public void Rewind_fetch_blocks_overlapping_submission_and_preserves_the_draft()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build(); var loop = new TgChatLoop(window);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = 0; var checkedGuard = false;
        loop.SetSubmissionSnapshotFactory(_ => { snapshots++; return null; });
        loop.SetRewindCommandHandler(async (_, ct) => await release.Task.WaitAsync(ct));
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            window.Composer.Input.Text = "/rewind"; loop.Submit();
            window.Composer.Input.Text = "draft with pending attachments"; loop.Submit();
            checkedGuard = snapshots == 0 && window.Composer.Input.Text == "draft with pending attachments" && window.Hints.FeedbackIsError;
            release.TrySetResult(); loop.Stop(); app.RequestStop(); return false;
        });
        app.Run(window); Assert.True(checkedGuard);
    }

    [Fact]
    public void Uncertain_history_guard_keeps_the_draft_and_does_not_create_a_submission()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build(); var loop = new TgChatLoop(window);
        var snapshots = 0; var checkedGuard = false;
        loop.SetSubmissionSnapshotFactory(_ => { snapshots++; return null; });
        loop.SetSubmissionGuard(_ => "history_reload_required: use /sessions id:s");
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            window.Composer.Input.Text = "keep my draft"; loop.Submit();
            checkedGuard = window.Composer.Input.Text == "keep my draft" && snapshots == 0 && window.Hints.FeedbackIsError;
            loop.Stop(); app.RequestStop(); return false;
        });
        app.Run(window); Assert.True(checkedGuard);
    }
}
