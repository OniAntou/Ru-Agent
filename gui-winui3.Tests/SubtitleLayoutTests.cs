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

    [Theory]
    [InlineData(24, 96, 24)]
    [InlineData(24, 144, 16)]
    [InlineData(24, 192, 12)]
    [InlineData(24, 0, 24)]
    public void FontSize_ConvertsPhysicalPixelsToEffectivePixels(
        int physicalSize,
        int dpi,
        int expected)
    {
        Assert.Equal(expected, SubtitleLayout.ToEffectiveFontSize(physicalSize, dpi));
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

    [Theory]
    [InlineData(1920)]
    [InlineData(-1920)]
    public void Rect_UsesTheTargetMonitorWorkAreaOrigin(int monitorX)
    {
        var workArea = new RectPx(monitorX, 40, 1920, 1040);
        var vts = new RectPx(monitorX + 260, 160, 640, 700);

        var result = SubtitleLayout.ComputeSubtitleRect(vts, workArea);

        Assert.InRange(result.Rect.X, workArea.X + 4, workArea.Right - result.Rect.Width - 4);
        Assert.InRange(result.Rect.Y, workArea.Y + 4, workArea.Bottom - result.Rect.Height - 4);
        Assert.Equal(vts.X + (vts.Width - result.Rect.Width) / 2, result.Rect.X);
    }

    [Fact]
    public void PlacementCacheSkipsUnchangedWindowUpdates()
    {
        var cache = new SubtitlePlacementCache();
        var rect = new RectPx(100, 200, 500, 80);

        Assert.True(cache.ShouldApply(rect, 24));
        Assert.False(cache.ShouldApply(rect, 24));
        Assert.True(cache.ShouldApply(rect with { X = 101 }, 24));
        Assert.True(cache.ShouldApply(rect with { X = 101 }, 25));

        cache.Reset();

        Assert.True(cache.ShouldApply(rect with { X = 101 }, 25));
    }
}
