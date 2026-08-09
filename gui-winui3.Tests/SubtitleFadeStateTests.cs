using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SubtitleFadeStateTests
{
    [Fact]
    public void NewTextDuringFadeCancelsFadeAndStaysVisible()
    {
        var state = new SubtitleFadeState();

        Assert.Equal(SubtitleFadeAction.Show, state.OnTextAvailable());
        Assert.Equal(SubtitleFadeAction.StartDelay, state.OnTextCleared());
        Assert.Equal(SubtitleFadeAction.BeginFade, state.OnDelayElapsed());

        Assert.Equal(SubtitleFadeAction.CancelFadeAndShow, state.OnTextAvailable());
        Assert.Equal(SubtitleFadeAction.None, state.OnFadeCompleted());
        Assert.True(state.IsVisible);
    }

    [Fact]
    public void NewTextAfterCompletedFadeShowsAgain()
    {
        var state = new SubtitleFadeState();
        state.OnTextAvailable();
        state.OnTextCleared();
        state.OnDelayElapsed();

        Assert.Equal(SubtitleFadeAction.Hide, state.OnFadeCompleted());
        Assert.False(state.IsVisible);

        Assert.Equal(SubtitleFadeAction.Show, state.OnTextAvailable());
        Assert.True(state.IsVisible);
    }
}
