namespace RuAgent.Cli.Tui;

/// <summary>A short, sparse shower on the shared welcome clock; no retained particles.</summary>
internal static class TuiShootingStars
{
    internal const int PeriodSeconds = 40;
    internal const int FirstShowerSeconds = 12;
    private const int TailSteps = 22;
    internal const int MaxCells = 3 * (TailSteps + 2);
    private const double TailFraction = 0.29;
    internal readonly record struct Cell(int Column, int Row, byte Dots, byte Color);

    // Sparse cells cover the full welcome viewport without another screen-sized
    // buffer. The native view merges them with its sky/lower stars before text.
    internal static int Collect(Span<Cell> cells, int columns, int rows, double time, out int active)
    {
        active = 0;
        if (columns <= 0 || columns > int.MaxValue / 2 || rows <= 0 || rows > int.MaxValue / 4
            || cells.Length < MaxCells || !double.IsFinite(time) || time < 0) return 0;
        time %= 86400;
        var wave = (int)(time / PeriodSeconds);
        var phase = time % PeriodSeconds - FirstShowerSeconds;
        // The last, staggered meteor must finish its extended tail too.
        if (phase < 0 || phase > 3.1) return 0;
        var width = columns * 2;
        var height = rows * 4;
        var count = 0;
        for (var slot = 0; slot < 2 + wave % 2; slot++)
        {
            var age = phase - slot * 0.65;
            var flight = 1.1 + slot * 0.13;
            if (age < 0 || age > flight * (1 + TailFraction)) continue;
            active++;
            var progress = age / flight;
            var startX = 0.22 + slot * 0.23 + wave % 5 * 0.016;
            var travelX = slot % 2 == 0 ? -0.18 : 0.18;
            for (var step = TailSteps; step >= 0; step--)
            {
                var u = progress - step / (double)TailSteps * TailFraction;
                var x = (int)Math.Round(width * (startX + travelX * u));
                var y = (int)Math.Round(height * (-0.03 + u * 1.03));
                var shade = step == 0 ? (byte)8 : step < 5 ? (byte)7 : step < 12 ? (byte)6 : (byte)3;
                Dot(cells, ref count, columns, rows, x, y, shade);
                if (step == 0) Dot(cells, ref count, columns, rows, x + 1, y, shade);
            }
        }
        return count;
    }

    private static void Dot(Span<Cell> cells, ref int count, int columns, int rows, int x, int y, byte color)
    {
        if (x < 0 || y < 0 || x >= columns * 2 || y >= rows * 4) return;
        var bit = (x & 1, y & 3) switch
        {
            (0, 0) => 1, (0, 1) => 2, (0, 2) => 4, (0, 3) => 64,
            (1, 0) => 8, (1, 1) => 16, (1, 2) => 32, _ => 128,
        };
        for (var i = 0; i < count; i++)
        {
            var cell = cells[i];
            if (cell.Column != x / 2 || cell.Row != y / 4) continue;
            cells[i] = cell with { Dots = (byte)(cell.Dots | bit), Color = Math.Max(cell.Color, color) };
            return;
        }
        cells[count++] = new Cell(x / 2, y / 4, (byte)bit, color);
    }
}
