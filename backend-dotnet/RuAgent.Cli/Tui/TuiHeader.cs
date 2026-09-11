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
    /// Composes the main landing/home screen rendered at startup and after /clear before chat begins.
    /// Features Cyberpunk-themed brand banner and a rounded System Overview card.
    /// </summary>
    public static IReadOnlyList<string> ComposeLandingScreen(
        int width,
        string workspace,
        string backend,
        string model,
        string preset)
    {
        if (width <= 0)
        {
            return Array.Empty<string>();
        }

        var w = Math.Clamp(width, 20, 1000);
        var lines = new List<string>();

        lines.Add(string.Empty);
        var banner = "  "
            + TuiPalette.Paint("✦ RUAGENT", TuiToken.Brand)
            + TuiPalette.Paint(" · Intelligent Coding Assistant", TuiToken.Meta)
            + TuiPalette.Paint(" [v2.0]", TuiToken.Brand2);
        lines.Add(banner);
        lines.Add(string.Empty);

        var inner = w - 2;
        var title = "─ System Overview ";
        var titleVisible = TuiText.VisibleWidth(title);
        var topFill = Math.Max(0, inner - titleVisible);

        var topBorder = TuiPalette.Paint("╭", TuiToken.Brand)
            + TuiPalette.Paint("─ ", TuiToken.Brand)
            + TuiPalette.Paint("System Overview", TuiToken.Brand2)
            + TuiPalette.Paint(" ", TuiToken.Brand)
            + TuiPalette.Paint(new string('─', topFill), TuiToken.Brand)
            + TuiPalette.Paint("╮", TuiToken.Brand);
        lines.Add(topBorder);

        var bodyInner = Math.Max(1, w - 4);

        string FormatRow(string label, string val, TuiToken valToken = TuiToken.Ink)
        {
            var lblPart = label + " : ";
            var lblWidth = TuiText.VisibleWidth(lblPart);
            var valBudget = Math.Max(1, bodyInner - lblWidth);
            var valShort = TuiText.Truncate(val, valBudget);
            var rowContent = TuiPalette.Paint(lblPart, TuiToken.Meta)
                + TuiPalette.Paint(valShort, valToken);
            var pad = Math.Max(0, bodyInner - lblWidth - TuiText.VisibleWidth(valShort));
            return TuiPalette.Paint("│ ", TuiToken.Brand)
                + rowContent
                + new string(' ', pad)
                + TuiPalette.Paint(" │", TuiToken.Brand);
        }

        var wsText = string.IsNullOrWhiteSpace(workspace) ? "(none)" : workspace;
        lines.Add(FormatRow("Workspace", wsText));

        var engineText = Join(backend, model);
        if (!string.IsNullOrWhiteSpace(preset))
        {
            engineText += $" · {preset}";
        }
        if (string.IsNullOrWhiteSpace(engineText))
        {
            engineText = "(default)";
        }
        lines.Add(FormatRow("Engine   ", engineText, TuiToken.Brand2));

        var shortcuts = "/help · /model · ↑↓ history · Tab · Ctrl+C";
        lines.Add(FormatRow("Shortcuts", shortcuts, TuiToken.Line));

        var status = "Ready · Waiting for prompt...";
        lines.Add(FormatRow("Status   ", status, TuiToken.Ok));

        var bottomBorder = TuiPalette.Paint("╰" + new string('─', inner) + "╯", TuiToken.Brand);
        lines.Add(bottomBorder);

        lines.Add(string.Empty);
        lines.Add(TuiPalette.Paint("  Type a request below to start, or /help for all commands.", TuiToken.Meta));

        return lines;
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
