namespace RuApp.Services;

/// <summary>
/// Pure Color-Wave palette extracted from ChatView (slice 2).
/// No UI, no I/O. Business logic unchanged.
/// </summary>
public static class AgentActivityPalette
{
    public const double BeamRadius = 0.35;
    public const double TintBlend = 0.85;

    public static Windows.UI.Color SampleSidebarAuroraColor(double u, double center, Windows.UI.Color baseColor)
    {
        var wave = SampleAuroraColor(u, center, baseColor);
        // The bright activity palette needs less tint over the light sidebar;
        // its pale peak must not wash dark session names into the background.
        return baseColor.R + baseColor.G + baseColor.B < 384 ? LerpColor(baseColor, wave, 0.4) : wave;
    }

    public static Windows.UI.Color SampleAuroraColor(double u, double center, Windows.UI.Color baseColor)
    {
        double dist = Math.Abs(u - center);
        if (dist >= BeamRadius)
        {
            return baseColor;
        }

        // Smooth bell-shaped intensity around the beam center
        double intensity = (1.0 - Math.Cos((1.0 - dist / BeamRadius) * Math.PI)) / 2.0;

        // Subtle Cyan-Violet Aurora palette:
        // Leading edge: soft ethereal cyan (#38BDF8)
        // Trailing edge: soft elegant indigo/violet (#818CF8)
        // Peak core: soft bright luminous shimmer (#E0F2FE)
        var cCyan = Windows.UI.Color.FromArgb(255, 56, 189, 248);
        var cViolet = Windows.UI.Color.FromArgb(255, 129, 140, 248);
        var cGlow = Windows.UI.Color.FromArgb(255, 224, 242, 254);

        Windows.UI.Color auroraTint;
        if (u < center)
        {
            double t = (center - u) / BeamRadius;
            auroraTint = LerpColor(cGlow, cCyan, t);
        }
        else
        {
            double t = (u - center) / BeamRadius;
            auroraTint = LerpColor(cGlow, cViolet, t);
        }

        return LerpColor(baseColor, auroraTint, intensity * TintBlend);
    }

    public static Windows.UI.Color LerpColor(Windows.UI.Color from, Windows.UI.Color to, double progress)
    {
        double smooth = (1.0 - Math.Cos(progress * Math.PI)) / 2.0;
        byte r = (byte)(from.R + (to.R - from.R) * smooth);
        byte g = (byte)(from.G + (to.G - from.G) * smooth);
        byte b = (byte)(from.B + (to.B - from.B) * smooth);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }
}
