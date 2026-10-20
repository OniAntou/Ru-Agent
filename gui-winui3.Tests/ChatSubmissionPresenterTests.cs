using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatSubmissionPresenterTests
{
    [Fact]
    public async Task Draft_clears_only_when_the_run_is_accepted_and_the_submitted_snapshot_is_still_current()
    {
        var clears = 0;
        bool current = true;
        bool accepted = await ChatSubmissionPresenter.SubmitAsync(
            onAccepted =>
            {
                onAccepted();
                return Task.FromResult(true);
            },
            () => current,
            () => clears++);
        Assert.True(accepted);
        Assert.Equal(1, clears);

        current = false;
        await ChatSubmissionPresenter.SubmitAsync(
            onAccepted => { onAccepted(); return Task.FromResult(true); },
            () => current,
            () => clears++);
        Assert.Equal(1, clears);

        await ChatSubmissionPresenter.SubmitAsync(
            onAccepted => { onAccepted(); return Task.FromResult(false); },
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
}
