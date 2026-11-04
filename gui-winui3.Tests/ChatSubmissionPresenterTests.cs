using RuApp.Services;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatSubmissionPresenterTests
{
    [Fact]
    public async Task Run_selection_is_captured_before_delayed_session_admission()
    {
        var selection = new CreateRunProviderSelection("models.dev:openai", "model-a");
        var ensureSession = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? session = null;
        var submission = ChatSubmissionPresenter.CaptureRunSubmissionAsync(
            () => selection,
            () => ensureSession.Task,
            () => session!);

        selection = new CreateRunProviderSelection("models.dev:anthropic", "model-b");
        session = "session-created";
        ensureSession.SetResult();
        var captured = await submission;

        Assert.NotNull(captured);
        Assert.Equal("session-created", captured.SessionId);
        Assert.Equal(new CreateRunProviderSelection("models.dev:openai", "model-a"), captured.ProviderSelection);
    }

    [Fact]
    public async Task Missing_run_selection_does_not_start_session_admission()
    {
        bool ensured = false;

        var submission = await ChatSubmissionPresenter.CaptureRunSubmissionAsync(
            () => null,
            () => { ensured = true; return Task.CompletedTask; },
            () => "session");

        Assert.Null(submission);
        Assert.False(ensured);
    }

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

        var captured = await ChatSubmissionPresenter.CaptureRunSubmissionAsync(
            () => new CreateRunProviderSelection("models.dev:openai", "gpt-test"),
            () => { session = "created"; return Task.CompletedTask; },
            () => session);

        Assert.Equal("created", captured?.SessionId);
        Assert.Equal(new CreateRunProviderSelection("models.dev:openai", "gpt-test"), captured?.ProviderSelection);
    }

    [Fact]
    public async Task Stop_cancellation_after_delayed_session_admission_prevents_a_submission_snapshot()
    {
        using var cancellation = new CancellationTokenSource();
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submission = ChatSubmissionPresenter.CaptureRunSubmissionAsync(
            () => new CreateRunProviderSelection("models.dev:openai", "gpt-test"),
            () => admission.Task,
            () => "created-session",
            cancellationToken: cancellation.Token);

        cancellation.Cancel();
        admission.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submission);
    }

    [Fact]
    public async Task Late_session_creation_cannot_own_a_newer_session_open_generation()
    {
        string session = "created-session";
        long generation = 4;
        var captured = await ChatSubmissionPresenter.CaptureRunSubmissionAsync(
            () => new CreateRunProviderSelection("models.dev:openai", "gpt-test"),
            () => Task.CompletedTask,
            () => session,
            () => generation);

        generation++;
        Assert.False(ChatSubmissionPresenter.OwnsAdmission(captured!, () => session, () => generation));

        generation--;
        session = "opened-session";
        generation++;

        Assert.NotNull(captured);
        Assert.False(ChatSubmissionPresenter.OwnsAdmission(captured, () => session, () => generation));
    }
}
