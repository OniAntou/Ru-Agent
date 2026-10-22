namespace RuAgent.Cli.Tui;

public sealed record DropdownItem(string Label, string Description, string? Detail = null, bool Enabled = true);

/// <summary>
/// The one selection surface: slash-command suggestions, approval choices and pickers all
/// render through this component so arrow-key behaviour and styling stay identical.
/// </summary>
public static class SlashDropdown
{
    public const int MinWidth = 24;
    public const int MaxWidth = 1000;
    public const int LabelColumn = 12;

    /// <summary>Cells kept for the description when rows carry long labels.</summary>
    public const int MinDetailReserve = 12;

    public static IReadOnlyList<string> Compose(
        int width,
        string title,
        IReadOnlyList<DropdownItem> items,
        int selected,
        int maxVisible,
        string? footer)
    {
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        var lines = new List<string> { Top(w, title) };
        var labelWidth = LabelColumnFor(w, items);

        if (items.Count == 0)
        {
            lines.Add(Row(w, labelWidth, new DropdownItem("", "no matches"), false, isPlaceholder: true));
        }
        else
        {
            var window = Window(items.Count, selected, Math.Max(1, maxVisible));
            for (var i = window.Start; i < window.End; i++)
            {
                lines.Add(Row(w, labelWidth, items[i], i == selected, isPlaceholder: false));
            }
        }

        lines.Add(Footer(w, footer, items.Count == 0 ? 0 : selected + 1, items.Count));
        return lines;
    }

    /// <summary>
    /// Label column sized from the longest label so full names show on wide
    /// terminals, never below the historic width, and always leaving room for
    /// the description (or the whole row when rows carry no description).
    /// Measured over every item so scrolling never reflows the column.
    /// </summary>
    public static int LabelColumnFor(int width, IReadOnlyList<DropdownItem> items)
    {
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        var inner = Math.Max(1, w - 4);
        var longest = 0;
        var hasDetail = false;
        foreach (var item in items)
        {
            longest = Math.Max(longest, TuiText.VisibleWidth(item.Label));
            if (!string.IsNullOrWhiteSpace(item.Description) || !string.IsNullOrWhiteSpace(item.Detail))
            {
                hasDetail = true;
            }
        }

        if (!hasDetail)
        {
            return Math.Max(LabelColumn, Math.Min(longest, Math.Max(LabelColumn, inner - 2)));
        }

        return Math.Max(LabelColumn, Math.Min(longest, Math.Max(LabelColumn, inner - MinDetailReserve - 3)));
    }

    /// <summary>The closing border, also usable stand-alone for list footers.</summary>
    public static string Footer(int width, string? footer, int current, int total) => Bottom(width, footer, current, total);

    /// <summary>Visible slice that always contains the selection.</summary>
    public static (int Start, int End) Window(int count, int selected, int maxVisible)
    {
        if (count <= maxVisible)
        {
            return (0, count);
        }

        var half = maxVisible / 2;
        var start = Math.Clamp(selected - half, 0, count - maxVisible);
        return (start, start + maxVisible);
    }

    private static string Top(int width, string title)
    {
        var inner = width - 2;
        var label = string.IsNullOrWhiteSpace(title) ? "select" : title.Trim();
        var left = "─ " + label + " ";
        if (TuiText.VisibleWidth(left) > inner - 1)
        {
            label = TuiText.Truncate(label, Math.Max(1, inner - 4));
            left = "─ " + label + " ";
        }

        return TuiPalette.Paint("╭", TuiToken.Brand)
            + TuiPalette.Paint("─ ", TuiToken.Brand)
            + TuiPalette.Paint(label, TuiToken.Meta)
            + TuiPalette.Paint(" ", TuiToken.Brand)
            + TuiPalette.Paint(new string('─', Math.Max(0, inner - TuiText.VisibleWidth(left))), TuiToken.Brand)
            + TuiPalette.Paint("╮", TuiToken.Brand);
    }

    private static string Row(int width, int labelWidth, DropdownItem item, bool isSelected, bool isPlaceholder)
    {
        var inner = Math.Max(1, width - 4);
        var prefix = isSelected ? "❯ " : "  ";
        var label = TuiText.Pad(TuiText.MiddleTruncate(item.Label, labelWidth), labelWidth);
        var rest = inner - 2 - labelWidth - 1;

        var description = rest <= 1
            ? string.Empty
            : TuiText.Truncate(
                string.IsNullOrWhiteSpace(item.Detail) ? item.Description : item.Description + " " + item.Detail,
                rest);

        var text = prefix + label + " " + description;
        text = TuiText.Pad(text, inner);

        var labelToken = !item.Enabled ? TuiToken.Line : isSelected ? TuiToken.Brand : TuiToken.Meta;
        var body = isPlaceholder
            ? TuiPalette.Paint(text, TuiToken.Line)
            : TuiPalette.Paint(prefix + label, labelToken)
                + TuiPalette.Paint(" ", TuiToken.Line)
                + TuiPalette.Paint(description, item.Enabled ? TuiToken.Ink : TuiToken.Line)
                + new string(' ', Math.Max(0, inner - TuiText.VisibleWidth(prefix + label) - 1 - TuiText.VisibleWidth(description)));

        return TuiPalette.Paint("│", TuiToken.Line) + " " + body + " " + TuiPalette.Paint("│", TuiToken.Line);
    }

    private static string Bottom(int width, string? footer, int current, int total)
    {
        var inner = width - 2;
        var counter = total > 0 ? $"{current}/{total}" : "0/0";
        var hint = string.IsNullOrWhiteSpace(footer) ? counter : footer.Trim() + " · " + counter;

        string left = string.Empty;
        string right = " " + hint + " ─";
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

        var fill = Math.Max(0, inner - TuiText.VisibleWidth(left) - TuiText.VisibleWidth(right));
        return TuiPalette.Paint("╰", TuiToken.Brand)
            + TuiPalette.Paint(new string('─', fill), TuiToken.Brand)
            + (right.Length == 0 ? string.Empty : TuiPalette.Paint(right[..^2], TuiToken.Meta) + TuiPalette.Paint(" ─", TuiToken.Brand))
            + TuiPalette.Paint("╯", TuiToken.Brand);
    }
}
