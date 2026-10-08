using Windows.Foundation;
using Windows.Graphics;

namespace RuApp.Services;

/// <summary>
/// The arithmetic between the AppWindow title bar's physical-pixel insets and
/// the XAML title bar's device-independent layout: the caption-button inset in
/// layout units, and the passthrough region an interactive title-bar control
/// needs so the system forwards pointer input to it instead of the drag region.
/// </summary>
public static class TitleBarLayout
{
    public static double InsetInLayoutUnits(double physicalInset, double rasterizationScale)
    {
        double inset = Math.Max(0, physicalInset);
        return rasterizationScale > 0 ? inset / rasterizationScale : inset;
    }

    public static RectInt32 ToPhysicalRect(Rect bounds, double rasterizationScale)
    {
        double scale = rasterizationScale > 0 ? rasterizationScale : 1;
        return new RectInt32(
            (int)Math.Round(Math.Max(0, bounds.X) * scale),
            (int)Math.Round(Math.Max(0, bounds.Y) * scale),
            (int)Math.Round(Math.Max(0, bounds.Width) * scale),
            (int)Math.Round(Math.Max(0, bounds.Height) * scale));
    }
}
