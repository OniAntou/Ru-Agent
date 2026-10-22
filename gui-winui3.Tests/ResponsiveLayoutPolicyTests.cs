using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ResponsiveLayoutPolicyTests
{
    [Theory]
    [InlineData(0, ResponsiveLayoutTier.Narrow)]
    [InlineData(899.99, ResponsiveLayoutTier.Narrow)]
    [InlineData(900, ResponsiveLayoutTier.Compact)]
    [InlineData(1279.99, ResponsiveLayoutTier.Compact)]
    [InlineData(1280, ResponsiveLayoutTier.Wide)]
    [InlineData(1920, ResponsiveLayoutTier.Wide)]
    public void Selects_the_expected_tier_at_each_boundary(double width, ResponsiveLayoutTier expected)
    {
        Assert.Equal(expected, ResponsiveLayoutPolicy.GetForWidth(width).Tier);
    }

    [Fact]
    public void Returns_the_wide_metrics()
    {
        var metrics = ResponsiveLayoutPolicy.GetForWidth(1440);

        Assert.Equal(ResponsiveLayoutTier.Wide, metrics.Tier);
        Assert.Equal(250, metrics.DefaultSidebarWidth);
        Assert.Equal(220, metrics.MinSidebarWidth);
        Assert.Equal(360, metrics.MaxSidebarWidth);
        Assert.Equal(36, metrics.PageHorizontalPadding);
    }

    [Fact]
    public void Clamps_a_saved_sidebar_width_to_the_current_tier()
    {
        var compact = ResponsiveLayoutPolicy.GetForWidth(1024);

        Assert.Equal(220, ResponsiveLayoutPolicy.ClampSidebarWidth(100, compact));
        Assert.Equal(250, ResponsiveLayoutPolicy.ClampSidebarWidth(250, compact));
        Assert.Equal(360, ResponsiveLayoutPolicy.ClampSidebarWidth(400, compact));
    }

    [Fact]
    public void Narrow_metrics_keep_the_full_sidebar_and_the_same_resize_bounds()
    {
        var narrow = ResponsiveLayoutPolicy.GetForWidth(640);

        Assert.Equal(250, narrow.DefaultSidebarWidth);
        Assert.Equal(220, narrow.MinSidebarWidth);
        Assert.Equal(360, narrow.MaxSidebarWidth);
        Assert.Equal(12, narrow.PageHorizontalPadding);
        Assert.Equal(360, ResponsiveLayoutPolicy.ClampSidebarWidth(400, narrow));
    }
}
