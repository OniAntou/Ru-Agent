using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SubtitleLayoutTests
{
    [Fact]
    public void FontSize_ScalesWithHeight_AndClamps()
    {
        Assert.Equal(14, SubtitleLayout.ComputeFontSize(300));
        Assert.Equal(34, SubtitleLayout.ComputeFontSize(1000));
        Assert.Equal(22, SubtitleLayout.ComputeFontSize(500));
    }

    [Fact]
    public void Rect_IsCenteredUnderWindow()
    {
        var r = SubtitleLayout.ComputeSubtitleRect(new RectPx(200, 100, 400, 500), 1920, 1080);
        Assert.Equal(400, r.Rect.Width);   // == vts width, in [260, 614]: no clamp
        Assert.Equal(200, r.Rect.X);       // centered: 200 + (400 - 400) / 2
        Assert.Equal(100 + 500 + 12, r.Rect.Y);
    }

    [Fact]
    public void Rect_OverlapsWhenBottomExceedsScreen()
    {
        var vts = new RectPx(0, 600, 400, 400);
        var r = SubtitleLayout.ComputeSubtitleRect(vts, 1920, 1000);
        Assert.True(r.Rect.Bottom <= 1000 - 4);
        Assert.True(r.Rect.Bottom < vts.Bottom); // overlaps on top of model
    }
}