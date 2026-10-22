namespace RuAgent.Cli.Tui;

/// <summary>One labelled line inside a panel.</summary>
public sealed record PanelRow(string Label, string? Detail = null);

/// <summary>A titled group of rows. The title is optional; rows are always shown.</summary>
public sealed record PanelSection(string? Title, IReadOnlyList<PanelRow> Rows);

/// <summary>
/// The framed information surface used by <c>/help</c> and <c>/status</c>: same border
/// language as the composer, label/detail columns, and every line exactly the requested
/// width so a panel can never wrap and push the transcript out of alignment.
/// </summary>
public static class TuiPanel
{
    public const int MinWidth = 24;
    public const int MaxWidth = 1000;
    public const int LabelColumn = 18;

    /// <summary>Below this inner width the detail column is dropped instead of squeezed.</summary>
    public const int MinDetailWidth = 6;

    public static IReadOnlyList<string> Compose(
        int width,
        string title,
        IReadOnlyList<PanelSection> sections,
        string? footer = null,
        bool framed = true)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        var lines = framed ? new List<string> { Top(w, title) } : new List<string>();
        string Row(string text) => framed ? Frame(w, text) : TuiText.Truncate(text, w);
        var firstSection = true;

        foreach (var section in sections)
        {
            if (!string.IsNullOrWhiteSpace(section.Title))
            {
                if (!firstSection)
                {
                    // A space preserves section separation through legacy captures that drop empty records.
                    lines.Add(framed ? Row(string.Empty) : " ");
                }

                lines.Add(Row(TuiPalette.Paint("  " + section.Title!.Trim(), TuiToken.Brand2)));
                firstSection = false;
            }

            foreach (var row in section.Rows)
            {
                lines.Add(ComposeRow(w, row, framed));
            }
        }

        if (framed) lines.Add(Bottom(w, footer));
        return lines;
    }

    private static string ComposeRow(int width, PanelRow row, bool framed)
    {
        var inner = Math.Max(1, width - (framed ? 4 : 0));
        var contentBudget = Math.Max(1, inner - 2);
        var label = TuiText.Pad(row.Label, Math.Min(LabelColumn, contentBudget));
        var labelWidth = TuiText.VisibleWidth(label);
        var detailWidth = contentBudget - labelWidth - 1;
        var detail = string.IsNullOrWhiteSpace(row.Detail) || detailWidth < MinDetailWidth
            ? string.Empty
            : TuiText.Truncate(row.Detail!.Trim(), detailWidth);

        var content = labelWidth >= contentBudget
            ? TuiPalette.Paint(label, TuiToken.Meta)
            : TuiPalette.Paint(label, TuiToken.Meta)
                + " "
                + TuiPalette.Paint(detail, TuiToken.Ink);

        return framed ? Frame(width, "  " + content) : "  " + content;
    }

    /// <summary>One content row: border, the already-styled content, padding, border.</summary>
    private static string Frame(int width, string content)
    {
        var inner = Math.Max(1, width - 4);
        var used = TuiText.VisibleWidth(content);
        var body = used > inner ? TuiText.Truncate(content, inner) : content;
        var pad = Math.Max(0, inner - TuiText.VisibleWidth(body));
        return TuiPalette.Paint("│", TuiToken.Line)
            + " "
            + body
            + new string(' ', pad)
            + " "
            + TuiPalette.Paint("│", TuiToken.Line);
    }

    private static string Top(int width, string title)
    {
        var inner = width - 2;
        var label = string.IsNullOrWhiteSpace(title) ? "info" : title.Trim();
        var left = "─ " + label + " ";
        if (TuiText.VisibleWidth(left) > inner - 1)
        {
            label = TuiText.Truncate(label, Math.Max(1, inner - 4));
            left = "─ " + label + " ";
        }

        var fill = Math.Max(0, inner - TuiText.VisibleWidth(left));
        return TuiPalette.Paint("╭", TuiToken.Line)
            + TuiPalette.Paint("─ ", TuiToken.Line)
            + TuiPalette.Paint(label, TuiToken.Brand)
            + TuiPalette.Paint(" ", TuiToken.Line)
            + TuiPalette.Paint(new string('─', fill), TuiToken.Line)
            + TuiPalette.Paint("╮", TuiToken.Line);
    }

    private static string Bottom(int width, string? footer)
    {
        var inner = width - 2;
        var hint = string.IsNullOrWhiteSpace(footer) ? string.Empty : footer.Trim();

        string right = hint.Length > 0 ? " " + hint + " ─" : string.Empty;
        while (TuiText.VisibleWidth(right) > inner - 1)
        {
            if (hint.Length > 1)
            {
                hint = TuiText.SafeCut(hint, hint.Length - 1);
                right = " " + hint + " ─";
                continue;
            }

            right = string.Empty;
            break;
        }

        var fill = Math.Max(0, inner - TuiText.VisibleWidth(right));
        return TuiPalette.Paint("╰", TuiToken.Line)
            + TuiPalette.Paint(new string('─', fill), TuiToken.Line)
            + (right.Length == 0
                ? string.Empty
                : TuiPalette.Paint(right[..^2], TuiToken.Meta) + TuiPalette.Paint(" ─", TuiToken.Line))
            + TuiPalette.Paint("╯", TuiToken.Line);
    }
}
