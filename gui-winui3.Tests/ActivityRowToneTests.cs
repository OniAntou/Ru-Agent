using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ActivityRowToneTests
{
    [Theory]
    [InlineData(false, false, ActivityRowTone.Muted)]
    [InlineData(false, true, ActivityRowTone.Ink)]
    [InlineData(true, false, ActivityRowTone.Danger)]
    [InlineData(true, true, ActivityRowTone.Danger)]
    public void A_settled_step_recedes_a_wait_stays_ink_and_a_failure_stays_danger(bool failed, bool waiting, string expected)
    {
        Assert.Equal(expected, ActivityRowTone.BrushKeyFor(failed, waiting));
    }

    [Fact]
    public void The_feed_paints_settled_rows_through_the_tone_rule()
    {
        string body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "ApplyActivityTextStyle");
        Assert.Contains("ActivityRowTone.BrushKeyFor(item.IsFailed, item.IsWaiting)", body, StringComparison.Ordinal);
    }
}
