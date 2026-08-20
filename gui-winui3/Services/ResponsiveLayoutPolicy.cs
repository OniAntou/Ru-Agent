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
    double NarrowRailWidth,
    double PageHorizontalPadding);

public static class ResponsiveLayoutPolicy
{
    public static ResponsiveLayoutMetrics GetForWidth(double width)
    {
        if (double.IsNaN(width))
        {
            width = 0;
        }

        if (width < 900)
        {
            return new(ResponsiveLayoutTier.Narrow, 72, 72, 72, 72, 12);
        }

        if (width < 1280)
        {
            return new(ResponsiveLayoutTier.Compact, 210, 180, 320, 72, 24);
        }

        return new(ResponsiveLayoutTier.Wide, 250, 180, 400, 72, 36);
    }

    public static double ClampSidebarWidth(double width, ResponsiveLayoutMetrics metrics)
        => Math.Clamp(width, metrics.MinSidebarWidth, metrics.MaxSidebarWidth);
}
