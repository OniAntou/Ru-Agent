using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatSessionPresentationTests
{
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Last_click_wins_regardless_of_stop_completion_order(bool firstFinishesLast)
    {
        using var session = new ChatSessionPresentation();
        session.Activate("original");
        var firstStop = Gate();
        var secondStop = Gate();
        var savedUnder = new List<string>();
        bool Save() { savedUnder.Add(session.SessionId); return true; }
        var first = session.OpenAsync("first", () => true, () => firstStop.Task, Save);
        var second = session.OpenAsync("second", () => true, () => secondStop.Task, Save);

        Assert.Equal("original", session.SessionId);
        Assert.Empty(savedUnder);
        if (!firstFinishesLast)
        {
            firstStop.SetResult();
            Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("original", session.SessionId);
            Assert.Empty(savedUnder);
        }
        secondStop.SetResult();
        var opened = await second.WaitAsync(TimeSpan.FromSeconds(5));
        if (firstFinishesLast) firstStop.SetResult();
        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("second", session.SessionId);
        Assert.Equal(new[] { "original" }, savedUnder);
        Assert.True(session.IsCurrent(opened!.Value.Generation));
    }

    [Fact]
    public async Task Session_bootstrap_does_not_override_a_user_switch_waiting_for_stop()
    {
        using var session = new ChatSessionPresentation();
        var stop = Gate();
        bool retained = false;
        var pending = session.OpenAsync("clicked", () => true, () => stop.Task,
            () => retained = true);
        session.Activate("created");
        stop.SetResult();

        Assert.NotNull(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(retained);
        Assert.Equal("clicked", session.SessionId);
    }

    [Fact]
    public async Task New_open_cancels_the_previous_load_including_when_reopening_the_same_session()
    {
        using var session = new ChatSessionPresentation();
        var first = (await Open(session, "one"))!.Value;
        var second = (await Open(session, "one"))!.Value;

        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.False(session.IsCurrent(first.Generation));
        Assert.True(session.IsCurrent(second.Generation));
        Assert.False(second.Cancellation.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_rejected_draft_retention_keeps_the_current_session_and_load(bool canRetain, bool retained)
    {
        using var session = new ChatSessionPresentation();
        var original = (await Open(session, "original"))!.Value;
        bool stopped = false;
        var result = await session.OpenAsync("other", () => canRetain,
            () => { stopped = true; return Task.CompletedTask; }, () => retained);

        Assert.Null(result);
        Assert.Equal(canRetain, stopped);
        Assert.Equal("original", session.SessionId);
        Assert.True(session.IsCurrent(original.Generation));
        Assert.False(original.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task A_failed_stop_does_not_save_the_draft_or_take_session_ownership()
    {
        using var session = new ChatSessionPresentation();
        var original = (await Open(session, "original"))!.Value;
        bool saved = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenAsync("other", () => true,
            () => Task.FromException(new InvalidOperationException("stop failed")), () => saved = true));
        Assert.False(saved);
        Assert.True(session.IsCurrent(original.Generation));
    }

    [Fact]
    public void Completion_is_once_per_turn_and_cannot_finish_a_new_turn_or_reopened_session()
    {
        using var session = new ChatSessionPresentation();
        session.Activate("one");
        var first = session.TryBeginCompletion()!.Value;
        Assert.Null(session.TryBeginCompletion());
        Assert.True(session.IsCurrent(first));
        session.BeginTurn();
        Assert.False(session.IsCurrent(first));
        var second = session.TryBeginCompletion()!.Value;
        Assert.True(session.IsCurrent(second));
        session.Activate("one");
        Assert.False(session.IsCurrent(second));
        Assert.NotNull(session.TryBeginCompletion());
    }

    [Fact]
    public async Task Dispose_cancels_the_load_and_rejects_a_pending_switch()
    {
        var session = new ChatSessionPresentation();
        var opened = (await Open(session, "one"))!.Value;
        var stop = Gate();
        bool saved = false;
        var pending = session.OpenAsync("two", () => true, () => stop.Task, () => saved = true);
        session.Dispose();
        session.Dispose();
        stop.SetResult();

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(opened.Cancellation.IsCancellationRequested);
        Assert.False(session.IsCurrent(opened.Generation));
        Assert.False(saved);
        Assert.Null(session.TryBeginCompletion());
    }

    private static Task<ChatSessionOpen?> Open(ChatSessionPresentation session, string id) =>
        session.OpenAsync(id, () => true, () => Task.CompletedTask, () => true);
}
