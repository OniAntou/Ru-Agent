using System.Text;

namespace RuApp.Services;

/// <summary>Bounded, deterministic eight-dot cells; no UI or execution ownership.</summary>
internal static class StartupBrailleWave
{
    public static string Frame(int columns, int rows, double seconds)
    {
        columns = Math.Clamp(columns, 1, 128);
        rows = Math.Clamp(rows, 1, 56);
        if (!double.IsFinite(seconds)) seconds = 0;
        double phase = seconds * 0.20;
        double quietWidth = Math.Clamp(210.0 / (columns * 13), 0.22, 0.65);
        double quietHeight = Math.Clamp(120.0 / (rows * 22), 0.20, 0.85);
        // Column geometry is shared by every row; avoid repeating trigonometry
        // for every dot when rendering the denser desktop field.
        var lowerRidges = new double[columns * 2];
        var upperRidges = new double[columns * 2];
        for (int i = 0; i < lowerRidges.Length; i++)
        {
            double x = (i + 0.5) / lowerRidges.Length;
            lowerRidges[i] = 0.60 + 0.16 * Math.Sin(x * 7 - phase)
                + 0.07 * Math.Sin(x * 13 + phase * 0.7);
            upperRidges[i] = 0.23 + 0.10 * Math.Cos(x * 6 + phase);
        }
        var text = new StringBuilder((columns + 1) * rows);
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int bits = 0;
                for (int dy = 0; dy < 4; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    double x = (column + (dx + 0.5) / 2) / columns;
                    double y = (row + (dy + 0.5) / 4) / rows;
                    // Reserve a quiet ellipse for wordmark, status and recovery action.
                    double quietX = (x - 0.5) / quietWidth, quietY = (y - 0.5) / quietHeight;
                    double center = quietX * quietX + quietY * quietY;
                    if (center < 1) continue;
                    int sample = column * 2 + dx;
                    double lowerDistance = (y - lowerRidges[sample]) / 0.075;
                    double upperDistance = (y - upperRidges[sample]) / 0.045;
                    double ribbon = Math.Exp(-lowerDistance * lowerDistance);
                    double upper = Math.Exp(-upperDistance * upperDistance);
                    double edge = SmoothStep(Math.Min(x, 1 - x) / 0.10)
                        * SmoothStep(Math.Min(y, 1 - y) / 0.12);
                    double density = Math.Max(ribbon * 0.88, upper * 0.48)
                        * SmoothStep((center - 1) / 0.9) * edge;
                    // Fixed spatial dither prevents unrelated random flicker between frames.
                    int hash = ((column * 2 + dx) * 73 + (row * 4 + dy) * 151) % 257;
                    if (density > (hash + 0.5) / 257)
                        bits |= 1 << (dy == 3 ? 6 + dx : dy + dx * 3);
                }
                text.Append((char)(0x2800 + bits));
            }
            if (row < rows - 1) text.Append('\n');
        }
        return text.ToString();
    }

    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }
}
