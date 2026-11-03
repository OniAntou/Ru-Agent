using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatSubmissionPresenterTests
{
    [Fact]
    public async Task Draft_clears_when_the_Host_accepts_the_run_not_when_the_run_ends()
    {
        // B6: the send completes only when the run ends or parks. Clearing then left the
        // sent text in the box for the whole run, and Enter queued it again.
        var clears = 0;
        var runEnds = new TaskCompletionSource<bool>();
        Action? accept = null;

        var send = ChatSubmissionPresenter.SubmitAsync(
            onAccepted =>
            {
                accept = onAccepted;
                return runEnds.Task;
            },
            () => true,
            () => clears++);

        Assert.Equal(0, clears);
        Assert.NotNull(accept);
        accept();
        Assert.Equal(1, clears);
        Assert.False(send.IsCompleted);

        runEnds.SetResult(true);
        Assert.True(await send);
        Assert.Equal(1, clears);
    }

    [Fact]
    public async Task A_send_the_Host_never_accepts_keeps_the_draft()
    {
        // Declined Full Access, a run already in hand or a failed create never reach
        // acceptance, so the text stays where the user can send it again.
        var clears = 0;

        bool accepted = await ChatSubmissionPresenter.SubmitAsync(
            _ => Task.FromResult(false),
            () => true,
            () => clears++);

        Assert.False(accepted);
        Assert.Equal(0, clears);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ChatSubmissionPresenter.SubmitAsync(
            _ => throw new InvalidOperationException("create failed"),
            () => true,
            () => clears++));
        Assert.Equal(0, clears);
    }

    [Fact]
    public async Task Text_typed_before_acceptance_survives_it()
    {
        var clears = 0;
        bool composerStillHoldsWhatWasSent = true;

        bool accepted = await ChatSubmissionPresenter.SubmitAsync(
            onAccepted =>
            {
                composerStillHoldsWhatWasSent = false;
                onAccepted();
                return Task.FromResult(true);
            },
            () => composerStillHoldsWhatWasSent,
            () => clears++);

        Assert.True(accepted);
        Assert.Equal(0, clears);
    }

    [Fact]
    public async Task A_recovery_retry_that_accepts_again_clears_only_once()
    {
        var clears = 0;

        await ChatSubmissionPresenter.SubmitAsync(
            onAccepted =>
            {
                onAccepted();
                onAccepted();
                return Task.FromResult(true);
            },
            () => true,
            () => clears++);

        Assert.Equal(1, clears);
    }

    [Fact]
    public async Task Session_identity_is_captured_after_a_fresh_session_is_ensured()
    {
        string session = "before";

        var captured = await ChatSubmissionPresenter.CaptureSessionAsync(
            () => { session = "created"; return Task.CompletedTask; },
            () => session);

        Assert.Equal("created", captured);
    }

    [Fact]
    public async Task Run_submission_snapshot_captures_session_and_generation_after_admission()
    {
        string session = "before";
        long generation = 4;
        var captured = await ChatSubmissionPresenter.CaptureRunSubmissionAsync(
            () => { session = "created"; generation = 5; return Task.CompletedTask; },
            () => session,
            () => generation,
            CancellationToken.None);

        Assert.Equal("created", captured.SessionId);
        Assert.Equal(5, captured.SessionGeneration);
        Assert.True(ChatSubmissionPresenter.OwnsAdmission(captured, () => session, () => generation));
        generation++;
        Assert.False(ChatSubmissionPresenter.OwnsAdmission(captured, () => session, () => generation));
    }

    [Fact]
    public async Task Stop_cancellation_after_delayed_session_admission_prevents_a_snapshot()
    {
        using var cancellation = new CancellationTokenSource();
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submission = ChatSubmissionPresenter.CaptureRunSubmissionAsync(
            () => admission.Task,
            () => "created-session",
            () => 2,
            cancellation.Token);

        cancellation.Cancel();
        admission.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submission);
    }
}
