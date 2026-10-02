using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>
/// The compact aurora header: identity, context and the key hints, replacing the tall
/// Spectre panel.
/// </summary>
public static class TuiHeader
{
    private const string Hints = "/ commands · ↑↓ history · tab complete · ctrl+o expand · ctrl+c stop";

    public static IReadOnlyList<string> Compose(
        int width,
        string workspace,
        string backend,
        string model,
        string preset,
        string sessionId,
        bool empty)
    {
        if (width <= 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();
        if (empty)
        {
            lines.Add(TuiPalette.Paint("✦ Ru", TuiToken.Brand) + TuiPalette.Paint(" · coding agent", TuiToken.Meta));
            lines.Add(string.Empty);
            var emptyContext = Shorten(Join(backend, model), Math.Max(8, width - 2));
            var emptyPreset = string.IsNullOrWhiteSpace(preset) ? string.Empty : " · " + preset;
            var emptyWorkspace = string.IsNullOrWhiteSpace(workspace) ? string.Empty : " · " + ShortenWorkspace(workspace, Math.Max(8, width / 2));
            lines.Add("  " + emptyContext + TuiPalette.Paint(emptyPreset + emptyWorkspace, TuiToken.Line));
            lines.Add(string.Empty);
            lines.Add(TuiPalette.Paint("  Type a request to start, or /help for every command.", TuiToken.Meta));
            lines.Add(TuiPalette.Paint("  Try: \"explain the approve flow in the harness\"", TuiToken.Line));
            return Pad(lines, width);
        }

        var left = TuiPalette.Paint("✦ Ru", TuiToken.Brand) + TuiPalette.Paint(" · coding agent", TuiToken.Meta);
        var session = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : TuiPalette.Paint(ShortSession(sessionId), TuiToken.Meta);
        lines.Add(TwoColumn(left, session, width));

        var context = ShortenWorkspace(workspace, Math.Max(8, width / 2));
        var presetLabel = TuiPalette.Paint(string.IsNullOrWhiteSpace(preset) ? string.Empty : " · " + preset, TuiToken.Line);
        var engine = string.IsNullOrWhiteSpace(Join(backend, model))
            ? string.Empty
            : TuiPalette.Paint(Shorten(Join(backend, model), Math.Max(8, width / 2)), TuiToken.Brand2);
        lines.Add(TwoColumn(" " + context + presetLabel, engine, width));

        lines.Add(TuiPalette.Paint(new string('─', width), TuiToken.Line));
        lines.Add(TuiPalette.Paint(Shorten(" " + Hints, width), TuiToken.Line));
        return Pad(lines, width);
    }

    public static void Render(
        TuiSurface surface,
        int width,
        string workspace,
        string backend,
        string model,
        string preset,
        string sessionId,
        bool empty)
    {
        foreach (var line in Compose(width, workspace, backend, model, preset, sessionId, empty))
        {
            surface.WriteLine(line);
        }
    }

    /// <summary>
    /// Paints the pinned full-screen header into <paramref name="height"/> absolute rows
    /// starting at <paramref name="topRow"/> (1-based), then parks the cursor on the first row
    /// below it so a sequential write continues into the transcript region. Each row is erased
    /// first: a resize rebuild can otherwise leave old text of a different width behind.
    /// The two rows the layout reserves are identity/session and workspace/preset/engine.
    /// </summary>
    public static void RenderPinned(
        TuiSurface surface,
        int width,
        int topRow,
        string workspace,
        string backend,
        string model,
        string preset,
        string sessionId,
        int height)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var rows = Math.Max(0, height);
        var first = Math.Max(1, topRow);
        var lines = Compose(width, workspace, backend, model, preset, sessionId, empty: false);
        surface.Repaint(writer =>
        {
            var builder = new StringBuilder();
            for (var i = 0; i < rows && i < lines.Count; i++)
            {
                builder.Append($"\u001b[{first + i};1H");
                builder.Append("\u001b[2K");
                builder.Append(TuiText.Truncate(lines[i], width));
            }

            builder.Append($"\u001b[{first + rows};1H");
            writer.Write(builder.ToString());
        });
    }

    /// <summary>
    /// Composes the main landing/home screen rendered at startup and after /clear before chat begins.
    /// Slim brand line, engine pill and hints; on wide terminals two braille figures
    /// flank the block left and right. Every line fits the viewport width.
    /// </summary>
    public static IReadOnlyList<string> ComposeLandingScreen(
        int width,
        string workspace,
        string backend,
        string model,
        string preset)
    {
        return ComposeLandingScreen(width, workspace, backend, model, preset, 0);
    }

    /// <summary>
    /// Landing screen vertically centered for the given viewport height: blank rows push
    /// the block toward the middle while reserving room for the composer below.
    /// </summary>
    public static IReadOnlyList<string> ComposeLandingScreen(
        int width,
        string workspace,
        string backend,
        string model,
        string preset,
        int viewportRows,
        int epoch = 0)
    {
        if (width <= 0)
        {
            return Array.Empty<string>();
        }

        var w = Math.Clamp(width, 20, 1000);
        List<string> lines;
        if (TrySideWidths(w, out var leftWidth, out var rightWidth, out var middleWidth))
        {
            lines = MergeSideArt(
                BuildLandingContent(middleWidth, workspace, backend, model, preset, epoch),
                leftWidth,
                rightWidth,
                middleWidth,
                epoch);
        }
        else
        {
            lines = BuildLandingContent(w, workspace, backend, model, preset, epoch);
        }

        if (viewportRows > lines.Count + ComposerReserveRows)
        {
            var target = viewportRows - ComposerReserveRows;
            var topPad = (target - lines.Count) / 2;
            var bottomPad = target - lines.Count - topPad;
            for (var i = 0; i < topPad; i++)
            {
                lines.Insert(0, StarRow(w, 1000 + i, epoch));
            }

            for (var i = 0; i < bottomPad; i++)
            {
                lines.Add(StarRow(w, 2000 + i, epoch));
            }
        }

        return lines;
    }

    private static List<string> BuildLandingContent(int width, string workspace, string backend, string model, string preset, int epoch = 0)
    {
        var w = Math.Clamp(width, 20, 1000);
        var lines = new List<string> { StarRow(w, 900, epoch) };
        foreach (var row in ComposeStarfield(w, epoch))
        {
            lines.Add(row);
        }

        lines.Add(StarRow(w, 910 + lines.Count, epoch));
        lines.Add(CenterWithStars(TuiPalette.Paint("✦  R u  A g e n t", TuiToken.Brand), w, 920 + lines.Count, epoch));
        lines.Add(CenterWithStars(TuiPalette.Paint("intelligent coding assistant", TuiToken.Meta), w, 930 + lines.Count, epoch));
        lines.Add(StarRow(w, 940 + lines.Count, epoch));

        var engine = Join(backend, model);
        var pill = "◉ " + (string.IsNullOrWhiteSpace(engine) ? "(default)" : engine)
            + " ▾  /  ⎇ " + (string.IsNullOrWhiteSpace(preset) ? "standard" : preset);
        lines.Add(CenterWithStars(PaintPill(pill, engine), w, 950 + lines.Count, epoch));

        var place = string.IsNullOrWhiteSpace(workspace) ? string.Empty : ShortenWorkspace(workspace, Math.Max(8, w - 4));
        if (!string.IsNullOrWhiteSpace(place))
        {
            lines.Add(CenterWithStars(TuiPalette.Paint(TuiText.Truncate(place, w), TuiToken.Line), w, 960 + lines.Count, epoch));
        }

        lines.Add(StarRow(w, 970 + lines.Count, epoch));
        lines.Add(CenterWithStars(TuiPalette.Paint("Type a request below to start, or /help for all commands.", TuiToken.Meta), w, 980 + lines.Count, epoch));
        lines.Add(StarRow(w, 990 + lines.Count, epoch));
        return lines;
    }

    /// <summary>Minimum middle column that keeps the title and pill readable between the figures.</summary>
    public const int MinSideMiddleWidth = 30;

    private const int SideGap = 2;

    /// <summary>Column widths for the flanked layout, or false when the terminal is too narrow.</summary>
    public static bool TrySideWidths(int width, out int leftWidth, out int rightWidth, out int middleWidth)
    {
        leftWidth = ArtLeftWidth;
        rightWidth = ArtRightWidth;
        middleWidth = width - leftWidth - rightWidth - (SideGap * 2);
        return middleWidth >= MinSideMiddleWidth;
    }

    private static List<string> MergeSideArt(List<string> content, int leftWidth, int rightWidth, int middleWidth, int epoch = 0)
    {
        var rows = Math.Max(content.Count, Math.Max(ArtLeft.Length, ArtRight.Length));
        var contentTop = (rows - content.Count) / 2;
        var leftTop = (rows - ArtLeft.Length) / 2;
        var rightTop = (rows - ArtRight.Length) / 2;
        var lines = new List<string>(rows);
        for (var i = 0; i < rows; i++)
        {
            var seed = 3000 + i;
            var gapLeft = StarSegment(SideGap, seed, leftWidth, epoch);
            var gapRight = StarSegment(SideGap, seed, leftWidth + SideGap + middleWidth, epoch);
            var left = i >= leftTop && i - leftTop < ArtLeft.Length
                ? TuiPalette.Paint(TuiText.Pad(ArtLeft[i - leftTop].TrimEnd(), leftWidth), TuiToken.Brand2)
                : StarSegment(leftWidth, seed, 0, epoch);
            var mid = i >= contentTop && i - contentTop < content.Count
                ? content[i - contentTop]
                : StarSegment(middleWidth, seed, leftWidth + SideGap, epoch);
            var right = i >= rightTop && i - rightTop < ArtRight.Length
                ? TuiPalette.Paint(TuiText.Pad(ArtRight[i - rightTop].TrimEnd(), rightWidth), TuiToken.Brand2)
                : StarSegment(rightWidth, seed, leftWidth + SideGap + middleWidth + SideGap, epoch);
            lines.Add(left + gapLeft + mid + gapRight + right);
        }

        return lines;
    }

    private static int ArtLeftWidth => MaxArtWidth(ArtLeft);

    private static int ArtRightWidth => MaxArtWidth(ArtRight);

    private static int MaxArtWidth(string[] art)
    {
        var max = 0;
        foreach (var line in art)
        {
            max = Math.Max(max, TuiText.VisibleWidth(line.TrimEnd()));
        }

        return max;
    }

    /// <summary>Rows reserved below the landing block for the composer frame.</summary>
    public const int ComposerReserveRows = 6;

    /// <summary>Star glyphs for the landing field. User-supplied decorative set.</summary>
    public static readonly string[] StarGlyphs =
    [
        "﹢", "✷", "﹒", "✦", "˚", "♡", "◌", ".", "*",
    ];

    /// <summary>Starfield band rows. Deterministic per cell so redraws never flicker.</summary>
    public const int StarfieldRows = 5;

    /// <summary>
    /// Scattered starfield: fixed-seed hash per cell decides star, glyph and dim tone.
    /// Pure, so every repaint (resize, reopen) shows the same sky. Uniform density
    /// across the whole viewport so the background reads as one continuous field
    /// behind the title, pill and side figures.
    /// </summary>
    public static IReadOnlyList<string> ComposeStarfield(int width, int epoch = 0)
    {
        var w = Math.Clamp(width, 20, 1000);
        var rows = new List<string>(StarfieldRows);
        for (var row = 0; row < StarfieldRows; row++)
        {
            rows.Add(StarRow(w, row, epoch));
        }

        return rows;
    }

    /// <summary>Uniform background density across all rows for a full-screen field.</summary>
    public static int StarDensityDivisor(int row)
    {
        _ = row;
        return StarBackgroundDivisor;
    }

    /// <summary>Cells per star on average for the full-screen background.</summary>
    public const int StarBackgroundDivisor = 78;

    /// <summary>Idle delay between landing star reshuffles.</summary>
    public const int StarTwinkleIntervalMs = 10_000;

    /// <summary>One full-width background row. Exactly <paramref name="width"/> cells.</summary>
    public static string StarRow(int width, int seedRow, int epoch = 0) => StarSegment(width, seedRow, 0, epoch);

    /// <summary>
    /// Background segment with a column offset so left/middle/right slices of the same
    /// absolute row stay horizontally continuous. Wide glyphs advance by their cell
    /// width and never overflow the segment.
    /// </summary>
    public static string StarSegment(int width, int seedRow, int colOffset, int epoch = 0)
    {
        var w = Math.Clamp(width, 0, 1000);
        if (w <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var used = 0;
        var col = colOffset;
        while (used < w)
        {
            var roll = StarHash(seedRow, col, epoch);
            if (roll % StarBackgroundDivisor < 1)
            {
                var glyph = StarGlyphs[(roll >> 4) % StarGlyphs.Length];
                var glyphWidth = Math.Max(1, TuiText.VisibleWidth(glyph));
                if (used + glyphWidth > w)
                {
                    sb.Append(' ', w - used);
                    break;
                }

                var tone = (roll >> 8) % 4;
                var token = tone switch
                {
                    0 => TuiToken.Brand,
                    1 => TuiToken.Brand2,
                    2 => TuiToken.Meta,
                    _ => TuiToken.Bright,
                };
                sb.Append(TuiPalette.Paint(glyph, token));
                used += glyphWidth;
            }
            else
            {
                sb.Append(' ');
                used += 1;
            }

            col++;
        }

        return TuiText.Pad(sb.ToString(), w);
    }

    /// <summary>Centers text with star-filled side padding instead of blank spaces.</summary>
    public static string CenterWithStars(string text, int width, int seedRow, int epoch = 0)
    {
        var w = Math.Clamp(width, 20, 1000);
        var visible = TuiText.VisibleWidth(text);
        if (visible >= w)
        {
            return TuiText.Truncate(text, w);
        }

        var left = (w - visible) / 2;
        var right = w - visible - left;
        var leftStars = left > 0 ? StarSegment(left, seedRow, 0, epoch) : string.Empty;
        var rightStars = right > 0 ? StarSegment(right, seedRow, left + visible, epoch) : string.Empty;
        return leftStars + text + rightStars;
    }

    private static int StarHash(int row, int col, int epoch = 0)
    {
        unchecked
        {
            var h = 2166136261u;
            h = (h ^ (uint)row) * 16777619u;
            h = (h ^ (uint)col) * 16777619u;
            if (epoch != 0)
            {
                h ^= (uint)epoch * 2654435761u;
                h *= 16777619u;
            }

            h ^= h >> 13;
            return (int)(h & 0x7fffffff);
        }
    }

    /// <summary>Engine pill: bright engine and preset names against dim separators.</summary>
    public static string PaintPill(string pill, string engine)
    {
        var presetMarker = " ▾  /  ⎇ ";
        var markerAt = pill.IndexOf(presetMarker, StringComparison.Ordinal);
        if (markerAt >= 0)
        {
            var enginePart = pill[..markerAt];
            var presetPart = pill[(markerAt + presetMarker.Length)..];
            return TuiPalette.Paint("◉ ", TuiToken.Brand)
                + TuiPalette.Paint(enginePart["◉ ".Length..].Trim(), TuiToken.Bright)
                + TuiPalette.Paint(" ▾  /  ⎇ ", TuiToken.Line)
                + TuiPalette.Paint(presetPart.Trim(), TuiToken.Bright);
        }

        if (!string.IsNullOrWhiteSpace(engine) && pill.Contains(engine, StringComparison.Ordinal))
        {
            var head = pill[..pill.IndexOf(engine, StringComparison.Ordinal)];
            var tail = pill[(pill.IndexOf(engine, StringComparison.Ordinal) + engine.Length)..];
            return TuiPalette.Paint(head, TuiToken.Brand2)
                + TuiPalette.Paint(engine, TuiToken.Bright)
                + TuiPalette.Paint(tail, TuiToken.Brand2);
        }

        return TuiPalette.Paint(pill, TuiToken.Brand2);
    }

    /// <summary>Left landing figure (braille art, decorative only).</summary>
    private static readonly string[] ArtLeft =
    [
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠸",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⣀⣤⠦⠖⠒⢒⣶⣢⣄⠀⠀⠀⢱⠂",
        "⠀⠀⠀⠀⠀⠀⠀⢀⣦⠏⠀⠀⠀⠀⠀⠀⠨⣦⣇",
        "⠀⠀⠀⠀⠀⠀⢀⡟⠁⠀⠀⠀⠀⠀⠀⠀⠀⡊⡃⡇⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀",
        "⠀⠀⠀⠀⠀⠀⣾⡇⠀⠀⠀⠀⠀⠀⠀⠀⠀⡄⢇⡇",
        "⠀⠀⠀⠀⠀⠀⢹⡇⠀⠀⡐⡂⠀⠀⠀⠀⠀⡂⣸⠇",
        "⠀⠀⠀⠀⠀⠀⠀⠉⠛⠛⠋⠁⠀⠀⠀⠀⡜⡰⠃",
        "⠀⠀⠀⠀⠀⠀⡆⠀⠀⠀⠀⠀⠀⢀⣠⠎⠀⠀⡀⡤⠖⠒⠒⠴⣄",
        "⠀⠀⠀⠀⠀⢀⣀⣿⣄⡀⠀⠀⣀⡴⠋⠀⡠⡶⠉⠁⠀⠀⠀⠀⠀⡏⡇",
        "⠀⠀⠀⠀⠀⡄⠀⢸⠁⠀⠀⢀⡏⠁⠀⡜⡝⠀⠀⠀⠀⠀⠀⠀⠀⣸⡇",
        "⠀⠀⠀⠀⠀⡇⠀⠈⠀⠀⠀⡮⠀⠀⣂⡞⠀⠀⠀⠀⡔⡆⠀⠀⠀⣿⡇",
        "⠀⠀⠀⠤⢤⣟⠤⠄⠀⠀⠀⣇⠀⠀⢥⠃⠀⠀⠀⠀⣇⡁⠀⣀⣼⠏⠀⠈",
        "⠀⠀⠀⠀⠀⢸⠀⠀⠀⠀⠀⠉⡇⠀⡭⠀⠀⠀⠀⠀⠑⠛⠋⠋⠉",
        "⠀⠀⠀⠀⠄⠸⠀⠀⠀⠀⠀⠀⠈⡎⡃",
        "⠈⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣣⠇⠀⠀⢠⡆",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣀⠤⠊⠀⠀⡝⠹⡿⠃⠂⠂",
        "⠀⡀⠀⠀⠀⠀⠀⠀⠄⠊⠁⠀⠀⠀⠀⠀⠀⠀⠁",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠠",
    ];

    /// <summary>Right landing figure (braille art, decorative only).</summary>
    private static readonly string[] ArtRight =
    [
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⡆⠀⠀⠀⠀⠁⠀⠠⠄",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣀⣄⡄⡄⠀⠀⠀⠀⢸⣷⠀⠀⠀⠀⠀⢀⢧⡀",
        "⠀⠀⠀⠀⠀⠀⠀⡠⡊⠃⠁⠀⠀⠙⣣⠀⠀⠒⢋⡯⠉⠁⠀⠀⠀⢭⡿",
        "⠀⠀⠀⠀⠀⠀⢠⡇⠀⠀⠀⣧⠀⠀⣺⠀⠀⠀⢸⡇⠀⠀⠀⡐⣭⠛⡏",
        "⠀⠀⠀⠀⠀⠀⢸⣧⠀⠀⠀⠈⠧⠴⠃⠀⠀⠀⠀⠁⠀⣐⡅⡷⠃⣸⠇",
        "⠀⠀⠀⠀⠀⠀⠈⣷⠀⠀⠀⠀⠀⠀⠀⠀⠀⡀⡄⢖⣇⡏⠎⠁⠀⡾⡀",
        "⠀⠀⠀⠀⠀⠀⠀⠫⣷⡤⣤⠤⠤⠐⠂⠊⢉⡠⠚⠉⠀⠀⠀⠀⣴⡏⡄",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢀⣲⠟⠁⠀⠀⡆⠀⠀⠀⡧⠍⢧",
        "⠀⠀⠀⠀⡠⡒⠉⠀⠀⠀⠀⠀⡄⠀⠀⢠⡯⠃⠀⠀⡀⠜⡧⠄⠀⢐⣏⠀⠈⢅",
        "⠀⠀⠀⠸⣟⠀⠀⣀⡞⠀⠀⠤⡧⠂⢀⡟⠁⠀⠀⠀⠀⠀⠃⠀⠀⠺⡃⠀⠀⢨",
        "⠀⠀⠀⠀⠉⠓⠋⠉⠀⠀⠀⠀⠁⠀⣕⠁⠀⠀⠀⠀⣤⣄⠀⠀⠀⡫⠅",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⣗⠀⠀⠀⠀⡿⠉⠉⢳⠀⠀⡃⡃⣠⢤⡀",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠙⡄⠀⠀⠘⢇⡀⠀⣨⠀⠀⣍⡂⡇⠼⠇",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠙⠦⣀⣀⣀⣀⣤⠏⠀⠀⠖⡇⢧⣀⣀",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⢠⠀⠀⠀⠀⠀⠀⠁⠁⠉⠁⠀⠀⠀⢼⡇⠀⠉⠉",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠂⢺⠒⠀⠀⠀⣤⡦⣂⣒⡄⠀⠀⠀⠀⡽⡅",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠁⠀⠀⡆⣝⡽⠉⠀⠀⣻⡆⠀⠀⣠⡇⡅",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠨⡿⠁⠀⠠⠔⠋⠁⠀⣠⣟⡨⠃⠀⠀⣆",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⢿⣀⣀⣀⡠⣤⣠⡿⢋⠆⠁⠀⠈⢹⠓",
        "⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠛⠛⡣⠯⠟⠙⠊⠁",
    ];

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

    private static IReadOnlyList<string> Pad(List<string> lines, int width)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            lines[i] = TuiText.Pad(lines[i], width);
        }

        return lines;
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

    private static string Shorten(string text, int maxCells) => TuiText.Truncate(text, maxCells);

    private static string Join(string backend, string model)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return model ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return backend;
        }

        return backend + "/" + model;
    }

    private static string ShortSession(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[..8];
}
