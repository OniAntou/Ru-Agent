namespace RuAgent.Cli.Tui;

/// <summary>Welcome-only spiral galaxy, projected into a reusable Braille cell raster.</summary>
internal sealed class TuiGalaxySky
{
    internal const int FrameMilliseconds = 50;
    internal const int MaxColumns = 240;
    internal const int MaxRows = 100;
    internal const double CoreYFraction = 0.64;
    internal const double GalaxyScale = 1.5;
    internal const double RingScale = 1.1;
    internal const int ArmCount = 6;
    private const double DotAspect = 1.2;
    private const double ScreenTiltRadians = -8 * Math.PI / 180;
    private static readonly double ScreenCos = Math.Cos(ScreenTiltRadians);
    private static readonly double ScreenSin = Math.Sin(ScreenTiltRadians);
    private static readonly byte[] DotBits = [1, 2, 4, 64, 8, 16, 32, 128];
    private byte[] _dots = [];
    private byte[] _colors = [];
    private int _width;
    private int _height;
    internal int Columns { get; private set; }
    internal int Rows { get; private set; }
    internal ReadOnlySpan<byte> Dots => _dots;
    internal ReadOnlySpan<byte> Colors => _colors;

    internal void Render(int columns, int rows, double time)
    {
        columns = Math.Clamp(columns, 0, MaxColumns);
        rows = Math.Clamp(rows, 0, MaxRows);
        if (columns != Columns || rows != Rows)
        {
            Columns = columns; Rows = rows;
            _dots = new byte[columns * rows];
            _colors = new byte[columns * rows];
        }
        else { Array.Clear(_dots); Array.Clear(_colors); }
        if (columns == 0 || rows == 0) return;
        if (!double.IsFinite(time) || time < 0) time = 0;
        time %= 86400;
        _width = columns * 2;
        _height = rows * 4;
        DrawStars(time);
        var count = (int)(columns * rows * 1.8);
        var rotation = time * 0.045;
        DrawHalo(count, rotation);
        DrawArms(count, time, rotation);
        DrawCore(count, rotation);
    }

    private void DrawStars(double time)
    {
        for (var k = 0; k < Columns * Rows * 0.045; k++)
        {
            var brightness = Hash(k * 11);
            var prominent = brightness > 0.97;
            if (!prominent && Math.Sin(time * 0.3 + Hash(k * 7) * 6.28) <= -0.55) continue;
            var x = (int)(Hash(k * 3) * _width);
            var y = (int)(Hash(k * 3 + 1) * _height);
            Dot(x, y, brightness > 0.8 ? (byte)3 : (byte)1);
            if (prominent)
            {
                // A few steady foreground stars add depth without flashing streaks.
                Dot(x - 1, y, 2); Dot(x + 1, y, 2);
                Dot(x, y - 1, 3); Dot(x, y + 1, 3);
                Dot(x, y, Hash(k * 19) > 0.6 ? (byte)8 : (byte)7);
            }
        }
    }

    private void DrawHalo(int count, double rotation)
    {
        for (var k = 0; k < count * 0.20; k++)
        {
            var r = Math.Sqrt(Hash(k * 41 + 709)) * 1.1;
            var a = Hash(k * 43 + 810) * Math.Tau + rotation * 0.3;
            if (Hash(k * 13 + 937) < 0.5 * (1 - r * 0.6))
                Project(Math.Cos(a) * r, Math.Sin(a) * r, Hash(k * 19 + 501) > 0.8 ? (byte)3 : (byte)1,
                    sizeMultiplier: RingScale);
        }
    }

    private void DrawArms(int count, double time, double rotation)
    {
        for (var k = 0; k < count; k++)
        {
            var arm = k % ArmCount;
            var radialPhase = Fraction(Math.Pow(Hash(k * 31 + 103), 0.73) - time * 0.015);
            var r = 0.12 + radialPhase * 0.88;
            var armBase = arm * Math.Tau / ArmCount;
            var noise = Hash(k * 17 + 300) + Hash(k * 17 + 301) + Hash(k * 17 + 302) - 1.5;
            var knot = 0.5 + 0.5 * Math.Cos(r * 44 + armBase);
            var filament = (k / ArmCount % 3 - 1) * (0.035 + r * 0.035);
            // Layer fine ribbons inside a wider dust envelope; compress their
            // scatter gently at stellar knots without spawning more particles.
            var spread = k % 5 == 0 ? noise * (0.21 + r * 0.30)
                : filament + noise * (0.08 + r * 0.14) * (1 - knot * 0.35);
            // Leave a leading-edge dust lane instead of painting black over other stars.
            var laneCenter = -0.035 + 0.012 * Math.Sin(r * 28 + armBase);
            if (r > 0.25 && Math.Abs(spread - laneCenter) < 0.024 + r * 0.012
                && Hash(k * 7 + 404) > 0.12) continue;
            var angle = armBase + r * 5.5 + rotation + spread;
            var radius = r + noise * 0.026;
            // Fade particles at the inner/outer boundary before their radial phase wraps.
            var feather = Math.Pow(Math.Sin(Math.PI * radialPhase), 0.4);
            if (Hash(k * 53 + 600) > feather * 0.92) continue;
            var pulse = 0.85 + 0.15 * Math.Sin(time * 0.14 + Hash(k * 9 + 600) * 6.28);
            if (Hash(k * 71 + 9) > pulse) continue;
            var nearRidge = Math.Abs(spread) < 0.11;
            var light = Hash(k * 11 + 7);
            // Reserve ice-white glints for a few ridge stars, keeping a darker
            // dust envelope around them and a distinct warm nucleus.
            byte shade = r < 0.3 ? light > 0.55 ? (byte)7 : (byte)4
                : nearRidge ? knot > 0.82 && light > 0.55 ? (byte)5
                : light > 0.84 ? (byte)7 : light > 0.45 ? (byte)6 : (byte)3 : (byte)2;
            Project(Math.Cos(angle) * radius, Math.Sin(angle) * radius, shade,
                Hash(k * 67 + 5) > 0.997, sizeMultiplier: RingScale);
            if (r > 0.2 && (k / ArmCount & 15) == 0)
                Project(Math.Cos(angle + 0.012) * radius, Math.Sin(angle + 0.012) * radius, 2,
                    sizeMultiplier: RingScale);
        }
    }

    private void DrawCore(int count, double rotation)
    {
        for (var k = 0; k < count * 0.26; k++)
        {
            // Avoid piling most particles into a few solid Braille cells. Mix
            // continuous light levels through a loose, slightly irregular bulge.
            var r = Math.Pow(Hash(k * 23 + 1201), 0.65) * 0.24;
            var a = Hash(k * 47 + 1309) * Math.Tau + rotation * 0.25;
            var heat = Math.Exp(-r * r / (0.105 * 0.105));
            if (Hash(k * 13 + 1439) > 0.18 + 0.72 * heat) continue;
            var light = Hash(k * 29 + 1553);
            var shade = light < heat * 0.32 ? (byte)8 : light < heat * 0.70 ? (byte)7
                : light < 0.20 + heat * 0.65 ? (byte)5 : light < 0.65 ? (byte)4 : (byte)2;
            var edge = 1 + 0.10 * Math.Sin(a * 3 + 0.8) + 0.06 * Math.Cos(a * 5 - 0.4);
            var px = Math.Cos(a) * r * edge + 0.04 * r * Math.Cos(a * 2 + 0.3);
            var py = Math.Sin(a) * r * 1.25 * edge;
            Project(px, py, shade);
        }
        Project(0, 0, 8);
    }

    private void Project(double px, double py, byte color, bool bright = false, double sizeMultiplier = 1)
    {
        const double tilt = -0.28;
        var scale = GalaxyScale * sizeMultiplier;
        var localX = _width * 0.76 * scale * (px * Math.Cos(tilt) - py * Math.Sin(tilt)) * DotAspect;
        var localY = _height * 0.58 * scale * (px * Math.Sin(tilt) + py * Math.Cos(tilt)) * 0.54;
        // Rotate the flattened disc on the screen. Rotating only its unprojected
        // coordinates changes arm phase but leaves the elliptical outline level.
        var x = (int)Math.Round(_width * 0.50 + (localX * ScreenCos - localY * ScreenSin) / DotAspect, MidpointRounding.AwayFromZero);
        var y = (int)Math.Round(_height * CoreYFraction + localX * ScreenSin + localY * ScreenCos, MidpointRounding.AwayFromZero);
        Dot(x, y, color);
        if (bright) { Dot(x + 1, y, 7); Dot(x - 1, y, 6); Dot(x, y + 1, 6); }
    }

    private void Dot(int x, int y, byte color)
    {
        if (x < 0 || y < 0 || x >= _width || y >= _height) return;
        var cell = y / 4 * Columns + x / 2;
        _dots[cell] |= DotBits[(x & 1) * 4 + (y & 3)];
        if (color > _colors[cell]) _colors[cell] = color;
    }

    private static double Fraction(double value) => value - Math.Floor(value);

    // Same unsigned hash as the approved browser preview; no per-frame RNG objects.
    private static double Hash(int n)
    {
        unchecked
        {
            var h = (uint)((n + 17) * 374761393);
            h = (h ^ (h >> 13)) * 1274126177u;
            return (h ^ (h >> 16)) / 4294967296d;
        }
    }
}
