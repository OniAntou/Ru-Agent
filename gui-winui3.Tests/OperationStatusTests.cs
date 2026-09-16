using System.ComponentModel;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The busy flag and status line shared by the run and policy controllers: the
/// sequencing of an operation, and the guarantee that every assignment is marshalled.
/// </summary>
public sealed class OperationStatusTests
{
    /// <summary>
    /// Collects assignments instead of running them, so a test can prove the status
    /// went through the marshal rather than mutating bound state in place.
    /// </summary>
    private sealed class UiQueue
    {
        private readonly List<Action> _pending = new();

        public int PostCount { get; private set; }

        public void Post(Action action)
        {
            PostCount++;
            if (RunInline)
            {
                action();
            }
            else
            {
                _pending.Add(action);
            }
        }

        public bool RunInline { get; set; } = true;

        public void Drain()
        {
            var copy = _pending.ToArray();
            _pending.Clear();
            foreach (var action in copy)
            {
                action();
            }
        }
    }

    [Fact]
    public void A_fresh_status_is_idle_with_its_initial_line()
    {
        var status = new OperationStatus(_ => { }, "Policies not loaded.");

        Assert.False(status.IsBusy);
        Assert.Equal("Policies not loaded.", status.StatusText);
    }

    [Fact]
    public async Task A_successful_operation_announces_then_reports_its_outcome()
    {
        var queue = new UiQueue();
        var status = new OperationStatus(queue.Post, "Ready");
        var seenWhileRunning = new List<(bool Busy, string Line)>();

        await status.RunAsync(
            "Deleting rule-1...",
            () =>
            {
                seenWhileRunning.Add((status.IsBusy, status.StatusText));
                return Task.FromResult(42);
            },
            value => $"Loaded {value} rules.",
            ex => $"error: {ex.Message}");

        Assert.Equal([(true, "Deleting rule-1...")], seenWhileRunning);
        Assert.False(status.IsBusy);
        Assert.Equal("Loaded 42 rules.", status.StatusText);
    }

    [Fact]
    public async Task A_failing_operation_reports_the_error_and_still_returns_to_idle()
    {
        var queue = new UiQueue();
        var status = new OperationStatus(queue.Post, "Ready");

        await Assert.ThrowsAsync<InvalidOperationException>(() => status.RunAsync(
            "Loading...",
            () => Task.FromException<int>(new InvalidOperationException("backend down")),
            _ => "unreachable",
            ex => $"Load error: {ex.Message}"));

        Assert.False(status.IsBusy);
        Assert.Equal("Load error: backend down", status.StatusText);
    }

    [Fact]
    public async Task The_operation_exception_reaches_the_caller_unchanged()
    {
        // The original controllers re-threw so a caller could decide; swallowing it in
        // the helper would silently turn failures into successes.
        var status = new OperationStatus(_ => { }, "Ready");
        var original = new HttpRequestException("no route");

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => status.RunAsync(
            "Working...",
            () => Task.FromException<int>(original),
            _ => "ok",
            _ => "failed"));

        Assert.Same(original, thrown);
    }

    [Fact]
    public async Task Every_assignment_crosses_the_marshal()
    {
        // Assignments raised from continuations must not touch bound state off-thread.
        // Three assignments for a success: busy on, status outcome, busy off.
        var queue = new UiQueue();
        var status = new OperationStatus(queue.Post, "Ready");

        await status.RunAsync(
            "Working...",
            () => Task.FromResult(true),
            _ => "Done.",
            _ => "Failed.");

        Assert.Equal(3, queue.PostCount);
    }

    [Fact]
    public async Task A_failing_operation_also_marshals_all_three_assignments()
    {
        var queue = new UiQueue();
        var status = new OperationStatus(queue.Post, "Ready");

        await Assert.ThrowsAsync<InvalidOperationException>(() => status.RunAsync(
            "Working...",
            () => Task.FromException<bool>(new InvalidOperationException()),
            _ => "Done.",
            _ => "Failed."));

        Assert.Equal(3, queue.PostCount);
    }

    [Fact]
    public async Task Nothing_is_visible_until_the_marshal_runs()
    {
        // The point of the marshal: state changes only when the UI thread runs them.
        var queue = new UiQueue { RunInline = false };
        var status = new OperationStatus(queue.Post, "Ready");

        await status.RunAsync(
            "Deleting...",
            () => Task.FromResult(true),
            _ => "Deleted.",
            _ => "Failed.");

        Assert.Equal("Ready", status.StatusText);
        Assert.False(status.IsBusy);

        queue.Drain();

        Assert.Equal("Deleted.", status.StatusText);
        Assert.False(status.IsBusy);
    }

    [Fact]
    public void Setting_the_flag_runs_the_action_on_the_marshal()
    {
        var status = new OperationStatus(_ => { }, "Ready");
        status.SetBusy(true);

        // The marshal was a no-op, so nothing ran: that is the contract — the state
        // belongs to whoever the marshal hands the assignment to.
        Assert.False(status.IsBusy);
    }

    [Fact]
    public void Both_properties_raise_change_notifications()
    {
        // The marshal runs inline, which is what the controllers do when no
        // synchronization context was supplied.
        var status = new OperationStatus(action => action(), "Ready");
        var raised = new List<string?>();
        status.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        status.SetBusy(true);
        status.SetStatus("Working...");
        status.SetBusy(false);

        Assert.Equal(
            [nameof(OperationStatus.IsBusy), nameof(OperationStatus.StatusText), nameof(OperationStatus.IsBusy)],
            raised);
    }

    [Fact]
    public async Task Cancellation_is_reported_like_any_other_outcome()
    {
        // A cancelled operation is not an error path the UI treats specially here; the
        // busy flag still has to come down or the composer stays stuck.
        var status = new OperationStatus(action => action(), "Ready");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => status.RunAsync(
            "Cancelling...",
            () => Task.FromException<bool>(new OperationCanceledException()),
            _ => "Cancelled.",
            ex => $"Cancel error: {ex.Message}"));

        Assert.False(status.IsBusy);

        // The line names the failure; the runtime's own wording is not ours to pin.
        Assert.StartsWith("Cancel error: ", status.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_initial_line_does_not_leave_a_null_status()
    {
        var status = new OperationStatus(_ => { }, null!);

        Assert.Equal(string.Empty, status.StatusText);
    }
}
