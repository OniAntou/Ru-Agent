using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class NavigationContentSwapTests
{
    [Fact]
    public void Returning_to_the_current_page_cancels_a_pending_navigation_and_restores_visibility()
    {
        var chat = new object();
        var customize = new object();
        object? current = chat;
        var pending = new Queue<Action>();
        var visible = true;
        var swaps = new NavigationContentSwap();
        void Transition(Action apply) { visible = false; pending.Enqueue(apply); }
        swaps.Show(current, customize, next => current = next, Transition, () => visible = true);
        swaps.Show(current, chat, next => current = next, Transition, () => visible = true);
        Assert.True(visible);
        while (pending.TryDequeue(out var apply)) apply();
        Assert.Same(chat, current);
    }

    [Fact]
    public void Only_the_latest_destination_is_mounted_even_if_completions_arrive_out_of_order()
    {
        var initial = new object();
        var older = new object();
        var latest = new object();
        var callbacks = new List<Action>();
        var applied = new List<object?>();
        var swaps = new NavigationContentSwap();
        swaps.Show(initial, older, applied.Add, callbacks.Add, () => { });
        swaps.Show(initial, latest, applied.Add, callbacks.Add, () => { });
        callbacks[1]();
        callbacks[0]();
        Assert.Same(latest, Assert.Single(applied));
    }

    [Fact]
    public void Closing_the_window_rejects_a_pending_mount()
    {
        Action? callback = null;
        var mounted = false;
        var swaps = new NavigationContentSwap();
        swaps.Show(new object(), new object(), _ => mounted = true, apply => callback = apply, () => { });
        swaps.Cancel();
        callback!();
        Assert.False(mounted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_failure_during_mount_or_fade_in_cannot_leave_the_whole_page_transparent(bool mountFails)
    {
        var visible = false;
        var failure = new InvalidOperationException("synthetic layout/animation failure");
        var error = Assert.Throws<InvalidOperationException>(() => UiMotion.CompleteSwap(
            () => { if (mountFails) throw failure; },
            () => throw failure,
            () => visible = true));
        Assert.Same(failure, error);
        Assert.True(visible);
    }

    [Fact]
    public void A_normal_swap_mounts_before_revealing_the_content()
    {
        var steps = new List<string>();
        UiMotion.CompleteSwap(() => steps.Add("mount"), () => steps.Add("reveal"), () => steps.Add("reset"));
        Assert.Equal(["mount", "reveal"], steps);
    }
}
