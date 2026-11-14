using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ConversationWidthStateTests
{
    [Theory]
    [InlineData(1200, 720)]
    [InlineData(2000, 720)]
    [InlineData(800, 720)]
    [InlineData(700, 640)]
    public void Default_width_is_720_and_clamps_to_the_window(double viewport, double expected)
    {
        var state = new ConversationWidthState();
        state.SetViewportWidth(viewport);
        Assert.Equal(expected, state.ContentWidth);
    }

    [Fact]
    public void Dragging_is_capped_at_920_and_shrinking_the_window_clamps_it_temporarily()
    {
        var state = new ConversationWidthState();
        state.SetViewportWidth(2000);
        state.BeginDrag();
        state.PreviewWidth(2000);
        Assert.Equal(920, state.ContentWidth);
        Assert.Equal(920 + ConversationWidthState.HandleOutset * 2, state.HandleLayerWidth);
        Assert.Equal(920 + ConversationWidthState.ComposerGutter, state.ComposerWidth);

        state.SetViewportWidth(800);
        Assert.Equal(720, state.ContentWidth);

        state.SetViewportWidth(2000);
        Assert.Equal(920, state.ContentWidth);
    }

    [Fact]
    public void Cancel_restores_the_width_from_before_the_drag()
    {
        var state = new ConversationWidthState();
        state.SetViewportWidth(1200);
        state.BeginDrag();
        state.PreviewWidth(900);
        state.CancelDrag();
        Assert.Equal(720, state.ContentWidth);
    }

    [Fact]
    public void Minimum_is_640()
    {
        var state = new ConversationWidthState();
        state.SetViewportWidth(700);
        state.BeginDrag();
        state.PreviewWidth(2000);
        Assert.Equal(640, state.ContentWidth);
    }
}
