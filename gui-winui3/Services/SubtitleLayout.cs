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

    public static (RectPx Rect, int FontSize, bool Overlap) ComputeSubtitleRect(
        RectPx vts, int screenWidth, int screenHeight)
    {
        int fontSize = ComputeFontSize(vts.Height);
        int overlayHeight = fontSize * 2 * 2;
        int overlayWidth = Math.Clamp(vts.Width, 260, screenWidth * MaxWidthRatioPercent / 100);
        int x = vts.X + (vts.Width - overlayWidth) / 2;
        x = Math.Max(x, ScreenEdge);
        x = Math.Min(x, screenWidth - overlayWidth - ScreenEdge);

        int defaultY = vts.Bottom + MarginBelow;
        bool overlap = defaultY + overlayHeight > screenHeight - ScreenEdge;
        int y = overlap
            ? vts.Bottom - overlayHeight - MarginOverlap
            : defaultY;
        return (new RectPx(x, y, overlayWidth, overlayHeight), fontSize, overlap);
    }
}