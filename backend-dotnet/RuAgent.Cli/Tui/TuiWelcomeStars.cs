namespace RuAgent.Cli.Tui;

/// <summary>Sparse fixed stars for the space below the welcome galaxy, without particle storage.</summary>
internal static class TuiWelcomeStars
{
    internal static (byte Dots, byte Color) At(int column, int row, double time)
    {
        if (column < 0 || row < 0) return default;
        uint hash;
        unchecked
        {
            hash = (uint)(column * 374761393 ^ row * 668265263);
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            hash ^= hash >> 16;
        }
        if (hash % 29 != 0) return default;
        if (!double.IsFinite(time) || time < 0) time = 0;
        var phase = (hash >> 8) / 16777216d * Math.Tau;
        var glow = Math.Sin(time % 86400 * 0.45 + phase);
        var color = hash % 11 == 0 ? (byte)5 : hash % 7 == 0 ? (byte)4
            : hash % 5 == 0 ? (byte)6 : glow > 0.35 ? (byte)3 : (byte)2;
        var dots = (byte)(1 << (int)((hash >> 12) & 7));
        if (hash % 17 == 0) dots |= 0x12;
        return (dots, color);
    }
}
