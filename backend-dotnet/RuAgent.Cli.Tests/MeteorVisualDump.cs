using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// Dumps the landing sky as plain text so an image renderer can draw it exactly as a terminal
/// would: one line per painted cell with its colour and braille mask. Inert unless
/// <c>RU_METEOR_DUMP</c> names an output path, so the normal test run writes nothing.
/// </summary>
public sealed class MeteorVisualDump
{
    private const int Width = 80;

    private const int Height = 24;

    [Fact]
    public void Dump()
    {
        var dumpPath = Environment.GetEnvironmentVariable("RU_METEOR_DUMP");
        if (string.IsNullOrWhiteSpace(dumpPath))
        {
            return;
        }

        var lines = new List<string> { $"VIEW {Width} {Height}" };
        for (var tick = 0; tick < TuiHeader.MeteorTravelTicks; tick++)
        {
            lines.Add($"TICK {tick}");
            var painted = new Dictionary<(int Col, int Row), (int Mask, int R, int G, int B, bool IsStar)>();

            for (var row = 0; row < Height; row++)
            {
                for (var col = 0; col < Width; col++)
                {
                    if (TuiHeader.StarCell(col, row) is not { } star)
                    {
                        continue;
                    }

                    var (r, g, b) = TuiPalette.Rgb(star.Token);
                    painted[(col, row)] = (StarMask, r, g, b, true);
                }
            }

            foreach (var (col, row, glyph, token) in TuiHeader.MeteorCells(tick, Width, Height))
            {
                var (r, g, b) = TuiPalette.Rgb(token);
                painted[(col, row)] = (glyph - '⠀', r, g, b, false);
            }

            foreach (var ((col, row), (mask, r, g, b, isStar)) in painted.OrderBy(pair => pair.Key.Row).ThenBy(pair => pair.Key.Col))
            {
                lines.Add($"C {col} {row} {mask} {r} {g} {b} {(isStar ? 1 : 0)}");
            }
        }

        File.WriteAllLines(dumpPath, lines);
        Assert.True(File.Exists(dumpPath));
    }

    /// <summary>A star is a single mark in the middle of its cell, not a dot matrix.</summary>
    private const int StarMask = 0b0100_1010;
}