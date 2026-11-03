using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ContextHoverStateTests
{
    [Fact]
    public void Escape_inside_suppresses_synthetic_enter_until_a_real_exit_then_reentry()
    {
        var state = new ContextHoverState();
        state.ObservePointer(inside: true);
        Assert.True(state.CanOpen);

        state.Dismiss();
        state.ObservePointer(inside: true); // Popup transition raised a synthetic enter.
        Assert.False(state.CanOpen);

        state.ObservePointer(inside: false);
        Assert.False(state.CanOpen);
        state.ObservePointer(inside: true);
        Assert.True(state.CanOpen);
    }

    [Fact]
    public void Escape_outside_does_not_suppress_the_next_hover()
    {
        var state = new ContextHoverState();

        state.Dismiss();
        state.ObservePointer(inside: true);

        Assert.True(state.CanOpen);
    }

    [Fact]
    public void Pointer_exit_without_dismissal_allows_a_later_hover()
    {
        var state = new ContextHoverState();
        state.ObservePointer(inside: true);
        state.ObservePointer(inside: false);
        state.ObservePointer(inside: true);

        Assert.True(state.CanOpen);
    }
}
