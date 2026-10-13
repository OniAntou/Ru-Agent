using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class BackgroundTaskRegistryTests
{
    [Fact]
    public void Running_tasks_are_capped_per_session()
    {
        var registry = new BackgroundTaskRegistry();
        var runningTokens = new List<CancellationToken>();
        for (var index = 0; index < TerminalLimits.MaxBackgroundTasksPerSession; index++)
        {
            Assert.True(registry.TryStart("session-a", $"task-{index}", 60_000, isBackground: true, out var token));
            runningTokens.Add(token);
        }

        Assert.False(registry.TryStart("session-a", "one-too-many", 60_000, isBackground: true, out _));
        // Foreground launches reserve capacity too, because they can be
        // promoted to background after their wait window expires.
        Assert.False(registry.TryStart("session-a", "foreground", 60_000, isBackground: false, out _));
        Assert.All(runningTokens, token => Assert.False(token.IsCancellationRequested));
        // Another session keeps its own budget.
        Assert.True(registry.TryStart("session-b", "task", 60_000, isBackground: true, out _));
    }

    [Fact]
    public void Foreground_reservation_can_be_promoted_without_releasing_its_slot()
    {
        var registry = new BackgroundTaskRegistry();
        for (var index = 0; index < TerminalLimits.MaxBackgroundTasksPerSession - 1; index++)
        {
            Assert.True(registry.TryStart("session", $"background-{index}", 60_000, isBackground: true, out _));
        }

        Assert.True(registry.TryStart("session", "foreground", 60_000, isBackground: false, out var foregroundToken));
        registry.MarkBackground("session", "foreground");

        Assert.False(registry.TryStart("session", "new-task", 60_000, isBackground: true, out _));
        Assert.False(foregroundToken.IsCancellationRequested);
        Assert.True(registry.TryRead("session", "foreground", out var promoted));
        Assert.True(promoted.IsBackground);
    }

    [Fact]
    public void Running_task_capacity_can_be_configured_per_registry()
    {
        var registry = new BackgroundTaskRegistry(maxRunningTasksPerSession: 2);

        Assert.True(registry.TryStart("session", "first", 60_000, isBackground: true, out _));
        Assert.True(registry.TryStart("session", "second", 60_000, isBackground: false, out _));
        Assert.False(registry.TryStart("session", "third", 60_000, isBackground: true, out _));
        Assert.True(registry.TryStart("other-session", "first", 60_000, isBackground: true, out _));
    }

    [Fact]
    public void Live_text_pages_while_running_and_state_survives_completion()
    {
        var registry = new BackgroundTaskRegistry();
        Assert.True(registry.TryStart("session", "t1", 60_000, isBackground: true, out _));
        registry.Append("session", "t1", "hello ");
        registry.Append("session", "t1", "world");

        Assert.True(registry.TryRead("session", "t1", out var running));
        Assert.Equal(BackgroundTaskState.Running, running.State);
        Assert.Equal("hello world", running.Text);
        Assert.Null(running.ExitCode);
        Assert.True(running.IsBackground);

        registry.Complete("session", "t1", ToolResultStatuses.Succeeded, 0, "hello world");

        Assert.True(registry.TryRead("session", "t1", out var done));
        Assert.Equal(BackgroundTaskState.Exited, done.State);
        Assert.Equal(0, done.ExitCode);
        Assert.Equal("hello world", done.Text);
    }

    [Fact]
    public void Kill_marks_the_task_and_completion_reports_killed()
    {
        var registry = new BackgroundTaskRegistry();
        Assert.True(registry.TryStart("session", "t1", 60_000, isBackground: true, out var token));

        Assert.Equal(BackgroundKillOutcome.Killing, registry.Kill("session", "t1"));
        Assert.True(token.IsCancellationRequested);

        registry.Complete("session", "t1", ToolResultStatuses.Cancelled, null, string.Empty);

        Assert.True(registry.TryRead("session", "t1", out var killed));
        Assert.Equal(BackgroundTaskState.Killed, killed.State);
        Assert.Equal(BackgroundKillOutcome.NotRunning, registry.Kill("session", "t1"));
        Assert.Equal(BackgroundKillOutcome.Unknown, registry.Kill("session", "missing"));
    }

    [Fact]
    public async Task Lifetime_expiry_reports_expired()
    {
        var registry = new BackgroundTaskRegistry();
        Assert.True(registry.TryStart("session", "t1", 1, isBackground: true, out var token));
        // The 1 ms lifetime timer is a thread-pool callback; under a loaded
        // suite it can land later than a fixed delay (this flaked twice). Poll
        // to a bounded deadline instead: cancellation still must happen, only
        // the wait is no longer an assumption about scheduler latency.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!token.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(token.IsCancellationRequested);

        registry.Complete("session", "t1", ToolResultStatuses.Cancelled, null, string.Empty);

        Assert.True(registry.TryRead("session", "t1", out var expired));
        Assert.Equal(BackgroundTaskState.Expired, expired.State);
    }

    [Fact]
    public void Failed_start_reports_failed_state()
    {
        var registry = new BackgroundTaskRegistry();
        Assert.True(registry.TryStart("session", "t1", 60_000, isBackground: true, out _));
        registry.Complete("session", "t1", ToolResultStatuses.Failed, null, string.Empty);

        Assert.True(registry.TryRead("session", "t1", out var failed));
        Assert.Equal(BackgroundTaskState.Failed, failed.State);
    }

    [Fact]
    public void Completed_entries_are_evicted_beyond_the_cap()
    {
        var registry = new BackgroundTaskRegistry();
        var total = BackgroundTaskRegistry.MaxCompletedEntriesPerSession + 2;
        for (var index = 0; index < total; index++)
        {
            var id = $"task-{index}";
            Assert.True(registry.TryStart("session", id, 60_000, isBackground: true, out _));
            registry.Complete("session", id, ToolResultStatuses.Succeeded, 0, "done");
        }

        Assert.False(registry.TryRead("session", "task-0", out _));
        Assert.True(registry.TryRead("session", $"task-{total - 1}", out _));
    }

    [Fact]
    public void CancelSession_cancels_only_running_tasks_in_that_session()
    {
        var registry = new BackgroundTaskRegistry();
        Assert.True(registry.TryStart("session-a", "running-a1", 60_000, true, out var first));
        Assert.True(registry.TryStart("session-a", "running-a2", 60_000, true, out var second));
        Assert.True(registry.TryStart("session-a", "completed-a", 60_000, true, out var completed));
        Assert.True(registry.TryStart("session-b", "running-b", 60_000, true, out var otherSession));
        registry.Complete("session-a", "completed-a", ToolResultStatuses.Succeeded, 0, "done");

        registry.CancelSession("session-a");

        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
        Assert.False(otherSession.IsCancellationRequested);
        Assert.False(completed.IsCancellationRequested);
        Assert.True(registry.TryRead("session-a", "completed-a", out var retained));
        Assert.Equal(BackgroundTaskState.Exited, retained.State);
        Assert.True(registry.TryRead("session-b", "running-b", out var untouched));
        Assert.Equal(BackgroundTaskState.Running, untouched.State);
    }

    [Fact]
    public void RemoveSession_cancels_running_tasks_and_drops_all_entries_without_mutating_other_sessions()
    {
        var registry = new BackgroundTaskRegistry();
        Assert.True(registry.TryStart("session-a", "running-a", 60_000, true, out var removedToken));
        Assert.True(registry.TryStart("session-a", "completed-a", 60_000, true, out _));
        registry.Complete("session-a", "completed-a", ToolResultStatuses.Succeeded, 0, "done");
        Assert.True(registry.TryStart("session-b", "running-b", 60_000, true, out var otherSessionToken));
        Assert.True(registry.TryStart("session-b", "completed-b", 60_000, true, out _));
        registry.Complete("session-b", "completed-b", ToolResultStatuses.Succeeded, 0, "keep");

        registry.RemoveSession("session-a");

        Assert.True(removedToken.IsCancellationRequested);
        Assert.False(registry.TryRead("session-a", "running-a", out _));
        Assert.False(registry.TryRead("session-a", "completed-a", out _));
        Assert.False(otherSessionToken.IsCancellationRequested);
        Assert.True(registry.TryRead("session-b", "running-b", out var running));
        Assert.Equal(BackgroundTaskState.Running, running.State);
        Assert.True(registry.TryRead("session-b", "completed-b", out var completed));
        Assert.Equal("keep", completed.Text);
    }

    [Fact]
    public void CancelAll_cancels_running_tasks_in_every_session()
    {
        var registry = new BackgroundTaskRegistry();
        Assert.True(registry.TryStart("session-a", "running-a", 60_000, true, out var first));
        Assert.True(registry.TryStart("session-b", "running-b", 60_000, true, out var second));

        registry.CancelAll();

        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
        Assert.True(registry.TryRead("session-a", "running-a", out var retainedA));
        Assert.Equal(BackgroundTaskState.Running, retainedA.State);
        Assert.True(registry.TryRead("session-b", "running-b", out var retainedB));
        Assert.Equal(BackgroundTaskState.Running, retainedB.State);
    }

    [Fact]
    public void CancelSession_invokes_callbacks_outside_the_registry_lock()
    {
        var registry = new BackgroundTaskRegistry();
        Assert.True(registry.TryStart("session", "task", 60_000, true, out var token));
        using var callbackCompleted = new ManualResetEventSlim();
        var callbackWaitTimedOut = false;
        using var registration = token.Register(() =>
        {
            _ = Task.Run(() =>
            {
                registry.TryRead("session", "task", out _);
                callbackCompleted.Set();
            });

            callbackWaitTimedOut = !callbackCompleted.Wait(TimeSpan.FromSeconds(2));
        });

        registry.CancelSession("session");

        Assert.False(callbackWaitTimedOut);
    }
}
