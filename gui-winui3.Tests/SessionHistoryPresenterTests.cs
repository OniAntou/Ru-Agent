using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionHistoryPresenterTests
{
    [Theory]
    [InlineData(false, "once", "Deny")]
    [InlineData(true, "session", "AllowSessionDirectory")]
    [InlineData(true, "always", "AllowAlways")]
    [InlineData(true, "other", "AllowOnce")]
    public void Approval_scope_selects_one_controller_operation(bool approved, string scope, string expected) =>
        Assert.Equal(Enum.Parse<AgentApprovalOperation>(expected), SessionHistoryPresenter.ResolveApproval(approved, scope));

    [Fact]
    public async Task Approval_workflow_serializes_actions_and_cleans_up_after_success()
    {
        var presenter = new SessionHistoryPresenter();
        var events = new List<string>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = presenter.RunApprovalAsync(true, "session", async operation =>
        {
            events.Add($"command:{operation}");
            await completion.Task;
        }, () => true, status => events.Add($"status:{status}"), () => events.Add("refresh"),
            () => events.Add("monitor"), _ => events.Add("error"), () => events.Add("clear"));
        var second = presenter.RunInputAsync("answer", _ =>
        {
            events.Add("second-command");
            return Task.CompletedTask;
        }, () => true, _ => { }, () => { }, () => { }, _ => { }, () => { });

        Assert.True(presenter.IsActionPending);
        Assert.DoesNotContain("second-command", events);
        completion.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(new[]
        {
            "status:Submitting approval…", "refresh", "command:AllowSessionDirectory", "monitor", "clear",
        }, events);
        Assert.False(presenter.IsActionPending);
    }

    [Fact]
    public async Task Approval_failure_reports_only_to_current_owner_and_clears_action_state()
    {
        var presenter = new SessionHistoryPresenter();
        bool ownsController = true;
        var events = new List<string>();

        await presenter.RunApprovalAsync(false, null, _ => throw new InvalidOperationException("offline"),
            () => ownsController, _ => events.Add("status"), () => events.Add("refresh"),
            () => events.Add("monitor"), exception => events.Add($"error:{exception.Message}"), () => events.Add("clear"));

        Assert.Equal(new[] { "status", "refresh", "error:offline", "clear" }, events);
        Assert.False(presenter.IsActionPending);

        events.Clear();
        ownsController = false;
        await presenter.RunInputAsync("answer", _ => throw new InvalidOperationException("stale"),
            () => ownsController, _ => events.Add("status"), () => events.Add("refresh"),
            () => events.Add("monitor"), _ => events.Add("error"), () => events.Add("clear"));
        Assert.Empty(events);
        Assert.False(presenter.IsActionPending);
    }

    [Theory]
    [InlineData(true, false, false, true, true, "HistoryLoading")]
    [InlineData(false, true, false, true, true, "ActionPending")]
    [InlineData(false, false, true, true, true, "RunPending")]
    [InlineData(false, false, false, false, true, "RowUnavailable")]
    [InlineData(false, false, false, true, false, "RowUnavailable")]
    [InlineData(false, false, false, true, true, "Allowed")]
    public void Rewind_requires_an_idle_view_and_a_saved_row_owned_by_the_active_session(
        bool historyLoading,
        bool rewindInProgress,
        bool runPending,
        bool hasSavedMessageIndex,
        bool rowBelongsToActiveSession,
        string expected) =>
        Assert.Equal(Enum.Parse<RewindEligibility>(expected), SessionHistoryPresenter.EvaluateRewind(
            historyLoading,
            rewindInProgress,
            runPending,
            hasSavedMessageIndex,
            rowBelongsToActiveSession));

    [Fact]
    public async Task Rewind_is_refused_when_the_session_loses_ownership_during_the_write()
    {
        bool ownsSession = true;
        bool reloaded = false;
        var rewound = await SessionRewindWorkflow.RunAsync(
            "session-a", 4, () => ownsSession,
            (_, _) => { ownsSession = false; return Task.FromResult(true); },
            () => { reloaded = true; return Task.FromResult(true); });

        Assert.False(rewound);
        Assert.False(reloaded);
    }

    [Fact]
    public async Task Rewind_owns_eligibility_and_resets_after_failure()
    {
        var presenter = new SessionHistoryPresenter();
        var events = new List<string>();
        bool rewind = await presenter.RewindAsync("session-a", 7, false, true,
            true, () => true,
            (_, _) => { events.Add("write"); return Task.FromResult(true); },
            () => { events.Add("reload"); return Task.FromResult(true); }, false,
            () => events.Add("run-pending"), () => events.Add("unavailable"), () => events.Add("failure"),
            _ => events.Add("trace"));

        Assert.False(rewind);
        Assert.Equal(new[] { "run-pending" }, events);
        Assert.False(presenter.IsRewindInProgress);

        events.Clear();
        rewind = await presenter.RewindAsync("session-a", 7, false, false,
            true, () => true,
            (_, _) => { events.Add("write"); throw new InvalidOperationException("write failed"); },
            () => { events.Add("reload"); return Task.FromResult(true); }, false,
            () => events.Add("run-pending"), () => events.Add("unavailable"), () => events.Add("failure"),
            _ => events.Add("trace"));

        Assert.False(rewind);
        Assert.Equal(new[] { "write", "trace", "failure" }, events);
        Assert.False(presenter.IsRewindInProgress);
    }
}
