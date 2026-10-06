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
        for (var index = 0; index < TerminalLimits.MaxBackgroundTasksPerSession; index++)
        {
            Assert.True(registry.TryStart("session-a", $"task-{index}", 60_000, enforceCap: true, isBackground: true, out _));
        }

        Assert.False(registry.TryStart("session-a", "one-too-many", 60_000, enforceCap: true, isBackground: true, out _));
        // A foreground call registers cap-exempt: it only becomes a background
        // task when its wait window expires.
        Assert.True(registry.TryStart("session-a", "foreground", 60_000, enforceCap: false, isBackground: false, out _));
        // Another session keeps its own budget.
        Assert.True(registry.TryStart("session-b", "task", 60_000, enforceCap: true, isBackground: true, out _));
    }

    [Fact]
    public void Live_text_pages_while_running_and_state_survives_completion()
    {
        var registry = new BackgroundTaskRegistry();
        Assert.True(registry.TryStart("session", "t1", 60_000, enforceCap: true, isBackground: true, out _));
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
        Assert.True(registry.TryStart("session", "t1", 60_000, enforceCap: true, isBackground: true, out var token));

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
        Assert.True(registry.TryStart("session", "t1", 1, enforceCap: true, isBackground: true, out var token));
        await Task.Delay(100);
        Assert.True(token.IsCancellationRequested);

        registry.Complete("session", "t1", ToolResultStatuses.Cancelled, null, string.Empty);

        Assert.True(registry.TryRead("session", "t1", out var expired));
        Assert.Equal(BackgroundTaskState.Expired, expired.State);
    }

    [Fact]
    public void Failed_start_reports_failed_state()
    {
        var registry = new BackgroundTaskRegistry();
        Assert.True(registry.TryStart("session", "t1", 60_000, enforceCap: true, isBackground: true, out _));
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
            Assert.True(registry.TryStart("session", id, 60_000, enforceCap: true, isBackground: true, out _));
            registry.Complete("session", id, ToolResultStatuses.Succeeded, 0, "done");
        }

        Assert.False(registry.TryRead("session", "task-0", out _));
        Assert.True(registry.TryRead("session", $"task-{total - 1}", out _));
    }
}
