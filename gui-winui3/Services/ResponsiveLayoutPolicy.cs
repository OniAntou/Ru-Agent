namespace RuApp.Services;

public enum ResponsiveLayoutTier
{
    Wide,
    Compact,
    Narrow
}

public readonly record struct ResponsiveLayoutMetrics(
    ResponsiveLayoutTier Tier,
    double DefaultSidebarWidth,
    double MinSidebarWidth,
    double MaxSidebarWidth,
    double PageHorizontalPadding);

public static class ResponsiveLayoutPolicy
{
    public const double MinimumSidebarWidth = 220;
    public const double MaximumSidebarWidth = 360;
    public const double DefaultSidebarWidth = 250;
    public static ResponsiveLayoutMetrics GetForWidth(double width)
    {
        if (double.IsNaN(width))
        {
            width = 0;
        }

        if (width < 900)
        {
            return new(ResponsiveLayoutTier.Narrow, DefaultSidebarWidth, MinimumSidebarWidth, MaximumSidebarWidth, 12);
        }

        if (width < 1280)
        {
            return new(ResponsiveLayoutTier.Compact, DefaultSidebarWidth, MinimumSidebarWidth, MaximumSidebarWidth, 24);
        }

        return new(ResponsiveLayoutTier.Wide, DefaultSidebarWidth, MinimumSidebarWidth, MaximumSidebarWidth, 36);
    }

    public static double ClampSidebarWidth(double width, ResponsiveLayoutMetrics metrics)
        => double.IsFinite(width)
            ? Math.Clamp(width, metrics.MinSidebarWidth, metrics.MaxSidebarWidth)
            : metrics.DefaultSidebarWidth;
}
