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
    /// <summary>
    /// Converts a physical-pixel title bar metric (caption inset or caption
    /// height) into layout units; a nonpositive scale reads as raw pixels.
    /// </summary>
    public static double PixelsToLayoutUnits(double pixels, double rasterizationScale)
    {
        double value = Math.Max(0, pixels);
        return rasterizationScale > 0 ? value / rasterizationScale : value;
    }

    public static double InsetInLayoutUnits(double physicalInset, double rasterizationScale) =>
        PixelsToLayoutUnits(physicalInset, rasterizationScale);

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
