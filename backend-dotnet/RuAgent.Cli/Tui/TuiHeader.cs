using System.Globalization;
using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>
/// The pinned session header and the pre-chat home surface, cloned from OpenCode: a one-row
/// header with the session title and token tally, and a landing screen with a centered
/// aurora/gray block wordmark.
/// </summary>
public static class TuiHeader
{
    /// <summary>
    /// One header row: "# &lt;title&gt;" at the left edge and the usage cluster (grouped token
    /// count, context percentage, session cost) at the right edge. The title is shortened
    /// first; the cluster is only dropped when the row can no longer fit both.
    /// </summary>
    public static string Compose(
        int width,
        string? title,
        long? tokens,
        int? contextPercent = null,
        double? costUsd = null)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(3);
        if (tokens is > 0)
        {
            parts.Add(tokens.Value.ToString("N0", CultureInfo.InvariantCulture));
        }

        if (contextPercent is { } percent)
        {
            parts.Add($"{percent}%");
        }

        if (costUsd is { } cost)
        {
            parts.Add($"(${cost.ToString("0.00", CultureInfo.InvariantCulture)})");
        }

        var tally = parts.Count == 0
            ? string.Empty
            : TuiPalette.Paint(string.Join("  ", parts), TuiToken.Meta);
        var tallyWidth = TuiText.VisibleWidth(tally);
        var titleBudget = tallyWidth > 0 ? width - tallyWidth - 2 : width;
        var heading = string.IsNullOrWhiteSpace(title) ? "session" : title.Trim();
        var left = TuiPalette.Paint("# ", TuiToken.Bright) + TuiPalette.Paint(heading, TuiToken.Bright);
        if (TuiText.VisibleWidth(left) > titleBudget)
        {
            left = TuiText.Truncate(left, Math.Max(0, titleBudget));
        }

        return tallyWidth > 0 && titleBudget >= 2
            ? TwoColumn(left, tally, width)
            : TuiText.Pad(TuiText.Truncate(left, width), width);
    }

    /// <summary>
    /// Paints the pinned full-screen header into <paramref name="height"/> absolute rows
    /// starting at <paramref name="topRow"/> (1-based), then parks the cursor on the first row
    /// below it so a sequential write continues into the transcript region. Each row is erased
    /// first: a resize rebuild can otherwise leave old text of a different width behind.
    /// </summary>
    public static void RenderPinned(
        TuiSurface surface,
        int width,
        int topRow,
        string? title,
        long? tokens,
        int height,
        int? contextPercent = null,
        double? costUsd = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var rows = Math.Max(0, height);
        var first = Math.Max(1, topRow);
        var lines = new[] { Compose(width, title, tokens, contextPercent, costUsd) };
        surface.Repaint(writer =>
        {
            var builder = new StringBuilder();
            for (var i = 0; i < rows && i < lines.Length; i++)
            {
                builder.Append($"\u001b[{first + i};1H");
                builder.Append("\u001b[2K");
                builder.Append(TuiText.Truncate(lines[i], width));
            }

            builder.Append($"\u001b[{first + rows};1H");
            writer.Write(builder.ToString());
        });
    }

    /// <summary>Rows reserved below the landing block for the composer frame.</summary>
    public const int ComposerReserveRows = 1 + ComposerBox.StackHeight + 1;

    /// <summary>Rows of the block wordmark.</summary>
    public const int WordmarkRows = 4;

    /// <summary>Narrowest terminal that fits the aurora block wordmark.</summary>
    public const int MinWordmarkWidth = 35;

    /// <summary>
    /// The pre-chat home surface, cloned from OpenCode: the centered block wordmark with
    /// blank space above and below; the composer lives in the pinned footer below it. When
    /// <paramref name="viewportRows"/> has room to spare the block sits vertically centered.
    /// </summary>
    public static IReadOnlyList<string> ComposeLandingScreen(int width, int viewportRows = 0)
    {
        if (width <= 0)
        {
            return Array.Empty<string>();
        }

        var w = Math.Clamp(width, 20, 1000);
        var mark = Wordmark(w);
        var lines = new List<string>();
        if (viewportRows > mark.Count + ComposerReserveRows)
        {
            var target = viewportRows - ComposerReserveRows;
            var topPad = (target - mark.Count) / 2;
            var bottomPad = target - mark.Count - topPad;
            for (var r = 0; r < topPad; r++)
            {
                lines.Add(StarRow(r, w));
            }

            lines.AddRange(mark);
            for (var r = 0; r < bottomPad; r++)
            {
                lines.Add(StarRow(topPad + mark.Count + r, w));
            }

            return lines;
        }

        lines.AddRange(mark);
        return lines;
    }

    /// <summary>One cell in about this many carries a star: sparse warm sparkles on black.</summary>
    private const int StarDivisor = 140;

    private const string StarGlyphs = "✦✧⋆·⁺";

    /// <summary>
    /// Deterministic landing-sky star for a background cell, null for empty space. The hash
    /// pins each star to its cell, so repaints and resizes never make the sky flicker.
    /// </summary>
    public static (char Glyph, TuiToken Token)? StarCell(int col, int row)
    {
        uint hash = (uint)(col * 73856093) ^ (uint)(row * 19349663);
        hash ^= hash >> 13;
        hash *= 1274126177u;
        hash ^= hash >> 16;
        if (hash % StarDivisor != 0)
        {
            return null;
        }

        var glyph = StarGlyphs[(int)((hash / StarDivisor) % (uint)StarGlyphs.Length)];
        var tone = (hash / ((uint)StarDivisor * (uint)StarGlyphs.Length)) % 4;
        var token = tone switch
        {
            0 => TuiToken.Warn,
            1 => TuiToken.Bright,
            2 => TuiToken.Meta,
            _ => TuiToken.Line,
        };
        return (glyph, token);
    }

    /// <summary>Frame time the Terminal.Gui landing timer ticks at.</summary>
    public const int MeteorFrameMilliseconds = 18;

    /// <summary>Meteor flight ticks, then rest until its next turn, looping.</summary>
    public const int MeteorTravelTicks = 18;

    /// <summary>Path cells drawn behind the head.</summary>
    public const int MeteorTailSteps = 14;

    /// <summary>How many meteors the sky cycles through.</summary>
    public const int MeteorCount = 3;

    /// <summary>
    /// Idle time each meteor waits between flights. The meteors never fly together: they take
    /// turns, so this is the gap between one arriving and the next.
    /// </summary>
    public const int MeteorCooldownSeconds = 25;

    /// <summary>
    /// Cooldown in ticks. The takeoff of meteor <c>m</c> is offset by an equal share of it, so
    /// the three spread evenly around the loop instead of launching as a group.
    /// </summary>
    public static readonly int MeteorCooldownTicks =
        (MeteorCooldownSeconds * 1000) / MeteorFrameMilliseconds;

    /// <summary>
    /// Ticks to add to a meteor's clock so each one takes a different turn. Spreading the whole
    /// cooldown evenly around the loop keeps the gaps equal even when it does not divide by the
    /// meteor count.
    /// </summary>
    private static int MeteorOffset(int meteor) =>
        (int)((long)MeteorCooldownTicks * meteor / MeteorCount);

    /// <summary>
    /// Path cells behind the head that keep the leading tone. The head is the leading edge, so
    /// lighting cells ahead of it painted a second bright knot further down the path and split
    /// the streak in two.
    /// </summary>
    private const int MeteorHeadLitSteps = 2;

    /// <summary>Where each fall line takes off, as a percent of the viewport width.</summary>
    private static readonly int[] MeteorTakeoffColPercents = [62, 78, 44];

    private static readonly int[] MeteorTakeoffRowPercents = [8, 20, 4];

    /// <summary>
    /// A cell holds one glyph, and no single glyph draws several specks — that ceiling is what
    /// made every earlier streak look sparse. Braille breaks it: U+2800 plus a bitmask lights
    /// any of the eight dots in a two-by-four grid inside one cell. So the meteor is now a
    /// dot-matrix picture, speck density carrying the shape and the palette carrying the fade.
    /// </summary>
    private const int BrailleBase = 0x2800;

    /// <summary>
    /// Dot masks for the head and the tail, thickest first. Each is a two-by-four grid:
    /// <code>
    /// bit0 bit3   dot1 top-left,   dot4 top-right
    /// bit1 bit4   dot2 upper-left, dot5 upper-right
    /// bit2 bit5   dot3 lower-left, dot6 lower-right
    /// bit6 bit7   dot7 bottom-left, dot8 bottom-right
    /// </code>
    /// </summary>

    /// <summary>
    /// The meteors crossing the landing sky, each on its own phase of a shared loop, falling
    /// along a straight forty-five degree diagonal that eases from a drift into a dive. Empty
    /// for any tick where every meteor is at rest. Pure in tick/width/height: the Terminal.Gui
    /// view advances the tick on a timer and draws exactly these cells.
    /// </summary>
    public static IReadOnlyList<(int Col, int Row, char Glyph, TuiToken Token)> MeteorCells(
        int tick,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        var cells = new List<(int Col, int Row, char Glyph, TuiToken Token)>();
        for (var meteor = 0; meteor < MeteorCount; meteor++)
        {
            // Each meteor dedupes against itself only: sharing the set let a later meteor cut
            // holes in an earlier streak wherever the two crossed, which read as a broken tail.
            cells.AddRange(MeteorStreak(meteor, tick, width, height));
        }

        return cells;
    }

    /// <summary>
    /// One meteor's streak at <paramref name="tick"/>: its head cell plus the tail trailing up
    /// and to the right, and nothing once its cells leave the viewport. Empty while it rests.
    /// </summary>
    public static IReadOnlyList<(int Col, int Row, char Glyph, TuiToken Token)> MeteorStreak(
        int meteor,
        int tick,
        int width,
        int height)
    {
        var phase = ((tick + MeteorOffset(meteor)) % MeteorCooldownTicks + MeteorCooldownTicks)
            % MeteorCooldownTicks;
        if (phase >= MeteorTravelTicks || meteor < 0 || meteor >= MeteorCount || width <= 0 || height <= 0)
        {
            return [];
        }

        var path = MeteorPath(meteor, width, height);
        var last = path.Count - 1;
        if (last < 1)
        {
            return [];
        }

        var cells = new List<(int Col, int Row, char Glyph, TuiToken Token)>();
        var head = MeteorHead(meteor, tick, width, height);
        if (head is not { } leading)
        {
            return cells;
        }

        // Drawn head-first so the tail never claims a cell the head already holds. Cells off the
        // viewport are dropped, which is what eats the streak away one cell at a time as it goes.
        for (var i = leading.Index; i >= Math.Max(0, leading.Index - MeteorTailSteps); i--)
        {
            var (col, row) = path[i];
            if (col < 0 || col >= width || row < 0 || row >= height)
            {
                continue;
            }

            var behind = leading.Index - i;
            var token = behind <= MeteorHeadLitSteps ? TuiToken.Bright : TuiToken.Meta;
            cells.Add((col, row, MeteorSpeck(behind), token));
        }

        return cells;
    }

    /// <summary>
    /// One meteor's leading cell at <paramref name="tick"/>, or null while it rests or once its
    /// path is too short to fall. Exposed so the flight curve and the stagger between meteors
    /// can be pinned without reading back a painted cell.
    /// </summary>
    public static (int Index, int Col, int Row)? MeteorHead(int meteor, int tick, int width, int height)
    {
        var phase = ((tick + MeteorOffset(meteor)) % MeteorCooldownTicks + MeteorCooldownTicks)
            % MeteorCooldownTicks;
        if (phase >= MeteorTravelTicks || meteor < 0 || meteor >= MeteorCount)
        {
            return null;
        }

        var path = MeteorPath(meteor, width, height);
        var last = path.Count - 1;
        if (last < 1)
        {
            return null;
        }

        // Eases out of the take-off and accelerates into the landing, so the meteor drifts for a
        // moment and then dives instead of tracking one cell per tick the whole way.
        var eased = (double)phase / (MeteorTravelTicks - 1);
        var index = Math.Min(last, (int)Math.Round(last * eased * eased));
        var (col, row) = path[index];
        return (index, col, row);
    }

    /// <summary>
    /// Braille cell for one streak cell: four dots at the leading point thinning to the two-dot
    /// diagonal. Every mask keeps the diagonal lit, so the streak never breaks into loose specks.
    /// </summary>
    private static char MeteorSpeck(int stepsBehind)
    {
        // Density along the tail: densest at the head, thinning to the bare diagonal.
        var dots = stepsBehind switch
        {
            0 => SpeckDensest,
            <= 4 => SpeckMid,
            _ => SpeckThinnest,
        };

        return (char)(BrailleBase | MeteorMask(dots));
    }

    /// <summary>Dot counts, densest first: the head, then behind it, then the run-out.</summary>
    private const int SpeckThinnest = 2;

    private const int SpeckMid = 3;

    private const int SpeckDensest = 4;

    private const int SpeckFaintDots = 0b0100_1000;

    private const int SpeckMediumDots = 0b0101_1000;

    private const int SpeckDenseDots = 0b0101_1100;

    private static int MeteorMask(int dots) => dots switch
    {
        >= SpeckDensest => SpeckDenseDots,
        >= SpeckMid => SpeckMediumDots,
        _ => SpeckFaintDots,
    };

    /// <summary>
    /// The fall line: a straight forty-five degree diagonal from the take-off, one column left
    /// and one row down per step, so the streak is exactly the staircase of cells the meteor
    /// stands on. It runs <see cref="MeteorExitSteps"/> cells past the last on-screen row — long
    /// enough for the tail to clear the edge too, so head and tail both leave cell by cell
    /// instead of the tail blinking out when the flight ends.
    /// </summary>
    private static IReadOnlyList<(int Col, int Row)> MeteorPath(int meteor, int width, int height)
    {
        var takeoffCol = Math.Clamp((width * MeteorTakeoffColPercents[meteor]) / 100, 0, width - 1);
        var takeoffRow = Math.Clamp((height * MeteorTakeoffRowPercents[meteor]) / 100, 0, height - 1);
        var steps = Math.Min(takeoffCol, height - 1 - takeoffRow) + MeteorExitSteps;
        var path = new List<(int Col, int Row)>();
        for (var i = 0; i <= steps; i++)
        {
            path.Add((takeoffCol - i, takeoffRow + i));
        }

        return path;
    }

    /// <summary>
    /// Cells the head travels past the last row. Sized to the tail as well as the head: the tail
    /// is <see cref="MeteorTailSteps"/> cells long, so it needs that much runway plus its own
    /// length to clear the bottom edge before the flight ends.
    /// </summary>
    private const int MeteorExitSteps = MeteorTailSteps + 1;

    private static string StarRow(int row, int width)
    {
        var cells = new List<(char Glyph, TuiToken? Token)>(width);
        for (var col = 0; col < width; col++)
        {
            var star = StarCell(col, row);
            cells.Add(star is { } s ? (s.Glyph, (TuiToken?)s.Token) : (' ', null));
        }

        return PaintCells(cells);
    }

    /// <summary>
    /// The aurora block wordmark: "ru" in aurora green and "agent" in light gray, lit from
    /// the top-left — cells below a left-low/right-high diagonal stay bright, cells above
    /// it take the dim tone, so the shadow rises like a triangle toward the right.
    /// Falls back to plain text on narrow terminals.
    /// </summary>
    public static IReadOnlyList<string> Wordmark(int width)
    {
        if (width <= 0)
        {
            return Array.Empty<string>();
        }

        var w = Math.Clamp(width, 20, 1000);
        var lines = new List<string> { string.Empty };
        if (w < MinWordmarkWidth)
        {
            lines.Add(Center(TuiPalette.Paint("Ru Agent", TuiToken.Aurora), w));
            lines.Add(string.Empty);
            return lines;
        }

        for (var i = 0; i < WordmarkRows; i++)
        {
            lines.Add(Center(PaintCells(WordmarkRowCells(i)), w));
        }

        lines.Add(string.Empty);
        return lines;
    }

    /// <summary>Unpadded visible glyphs of one wordmark row.</summary>
    private static string RowGlyphs(int row) => RuWord[row] + " " + AgentWord[row];

    private static int GridWidth()
    {
        var gridWidth = 0;
        for (var i = 0; i < WordmarkRows; i++)
        {
            gridWidth = Math.Max(gridWidth, RowGlyphs(i).Length);
        }

        return gridWidth;
    }

    /// <summary>
    /// Cells of one wordmark row: base tone above the light diagonal, dim tone below it.
    /// The shadow height grows left to right, so the lit face forms a triangle. Shared by
    /// the classic rows and the Terminal.Gui view.
    /// </summary>
    public static IReadOnlyList<(char Glyph, TuiToken? Token)> WordmarkRowCells(int row)
    {
        var gridWidth = GridWidth();
        var grid = new List<string>();
        for (var i = 0; i < WordmarkRows; i++)
        {
            grid.Add(RowGlyphs(i).PadRight(gridWidth));
        }

        var ruWidth = RuWord[row].Length;
        var cells = new List<(char Glyph, TuiToken? Token)>(gridWidth);
        for (var j = 0; j < gridWidth; j++)
        {
            if (grid[row][j] == ' ')
            {
                cells.Add((' ', null));
                continue;
            }

            var isRu = j < ruWidth;
            var heightFromBottom = WordmarkRows - 1 - row;
            var shadowHeight = (double)j / Math.Max(1, gridWidth - 1) * WordmarkRows;
            var token = heightFromBottom < shadowHeight
                ? (isRu ? TuiToken.AuroraDim : TuiToken.Meta)
                : (isRu ? TuiToken.Aurora : TuiToken.Bright);
            cells.Add((grid[row][j], token));
        }

        return cells;
    }

    /// <summary>Paints cells, coalescing runs of one token into a single escape.</summary>
    private static string PaintCells(IReadOnlyList<(char Glyph, TuiToken? Token)> cells)
    {
        var text = new StringBuilder();
        var run = new StringBuilder();
        TuiToken? runToken = null;
        void Flush()
        {
            if (run.Length == 0)
            {
                return;
            }

            text.Append(runToken is { } token ? TuiPalette.Paint(run.ToString(), token) : run.ToString());
            run.Clear();
            runToken = null;
        }

        foreach (var (glyph, token) in cells)
        {
            if (token != runToken)
            {
                Flush();
            }

            runToken = token;
            run.Append(glyph);
        }

        Flush();
        return text.ToString();
    }

    private static readonly string[] RuWord =
    [
        "█▀▀█ █  █",
        "█▀▀▄ █  █",
        "█  █ █  █",
        "▀  ▀ ▀▀▀▀",
    ];

    private static readonly string[] AgentWord =
    [
        "█▀▀█ █▀▀▀ █▀▀▀ █  █ ▀█▀",
        "█▀▀█ █ ▀▀ █▀▀  █▄ █  █ ",
        "█  █ █  █ █    █ ▀█  █ ",
        "▀  ▀ ▀▀▀▀ ▀▀▀▀ ▀  ▀  ▀ ",
    ];

    /// <summary>The "ru" half of the aurora block wordmark, one string per row.</summary>
    public static IReadOnlyList<string> WordmarkRuRows => RuWord;

    /// <summary>The "agent" half of the aurora block wordmark, one string per row.</summary>
    public static IReadOnlyList<string> WordmarkAgentRows => AgentWord;

    private static string Center(string text, int width)
    {
        var visible = TuiText.VisibleWidth(text);
        if (visible >= width)
        {
            return TuiText.Truncate(text, width);
        }

        var left = (width - visible) / 2;
        return new string(' ', left) + text + new string(' ', width - visible - left);
    }

    private static string TwoColumn(string left, string right, int width)
    {
        if (right.Length == 0)
        {
            return TuiText.Pad(left, width);
        }

        var leftWidth = TuiText.VisibleWidth(left);
        var rightWidth = TuiText.VisibleWidth(right);
        var room = width - leftWidth - rightWidth;
        if (room < 2)
        {
            return TuiText.Pad(left, width);
        }

        return left + new string(' ', room) + right;
    }

    /// <summary>Keeps the tail of a path so the leaf stays visible on narrow terminals.</summary>
    public static string ShortenWorkspace(string path, int maxCells)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (TuiText.VisibleWidth(normalized) <= maxCells)
        {
            return normalized;
        }

        var segments = normalized.Split('/');
        var built = string.Empty;
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var candidate = segments[i] + (built.Length == 0 ? string.Empty : "/" + built);
            if (TuiText.VisibleWidth("…/" + candidate) > maxCells)
            {
                break;
            }

            built = candidate;
        }

        return built.Length == 0 ? TuiText.Truncate(normalized, maxCells) : "…/" + built;
    }
}
