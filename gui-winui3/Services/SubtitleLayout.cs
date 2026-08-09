using RuApp.Models;

namespace RuApp.Services;

public static class SubtitleLayout
{
    private const int MarginBelow = 12;
    private const int MarginOverlap = 8;
    private const int ScreenEdge = 4;
    private const int MaxWidthRatioPercent = 32;

    public static int ComputeFontSize(int vtsHeight) =>
        Math.Clamp((int)Math.Round(vtsHeight * 0.045), 14, 34);

    public static int ToEffectiveFontSize(int physicalFontSize, int dpi)
    {
        int effectiveDpi = dpi > 0 ? dpi : 96;
        return Math.Max(1, (int)Math.Round(physicalFontSize * 96.0 / effectiveDpi));
    }

    public static (RectPx Rect, int FontSize, bool Overlap) ComputeSubtitleRect(
        RectPx vts, int screenWidth, int screenHeight)
    {
        return ComputeSubtitleRect(vts, new RectPx(0, 0, screenWidth, screenHeight));
    }

    public static (RectPx Rect, int FontSize, bool Overlap) ComputeSubtitleRect(
        RectPx vts, RectPx workArea)
    {
        int fontSize = ComputeFontSize(vts.Height);
        int availableWidth = Math.Max(1, workArea.Width - (ScreenEdge * 2));
        int availableHeight = Math.Max(1, workArea.Height - (ScreenEdge * 2));
        int maxWidth = Math.Max(1, Math.Min(
            availableWidth,
            workArea.Width * MaxWidthRatioPercent / 100));
        int minWidth = Math.Min(260, maxWidth);
        int overlayWidth = Math.Clamp(vts.Width, minWidth, maxWidth);
        int overlayHeight = Math.Min(fontSize * 2 * 2, availableHeight);
        int x = vts.X + (vts.Width - overlayWidth) / 2;
        int minX = workArea.X + ScreenEdge;
        int maxX = Math.Max(minX, workArea.Right - overlayWidth - ScreenEdge);
        x = Math.Clamp(x, minX, maxX);

        int defaultY = vts.Bottom + MarginBelow;
        bool overlap = defaultY + overlayHeight > workArea.Bottom - ScreenEdge;
        int y = overlap
            ? vts.Bottom - overlayHeight - MarginOverlap
            : defaultY;
        int minY = workArea.Y + ScreenEdge;
        int maxY = Math.Max(minY, workArea.Bottom - overlayHeight - ScreenEdge);
        y = Math.Clamp(y, minY, maxY);
        return (new RectPx(x, y, overlayWidth, overlayHeight), fontSize, overlap);
    }
}
