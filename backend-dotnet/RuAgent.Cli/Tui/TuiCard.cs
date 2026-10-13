namespace RuAgent.Cli.Tui;

/// <summary>
/// What a card row is, so the view picks its colours from the role instead of parsing text.
/// </summary>
public enum CardRowKind
{
    Spacer,

    /// <summary>The bold card heading; its right side carries the dismiss hint.</summary>
    Title,

    /// <summary>A dim horizontal rule under the heading, separating it from the body.</summary>
    Rule,

    /// <summary>The filter field; its label is what has been typed so far.</summary>
    Search,

    /// <summary>A plain input row (the masked secret), painted bright like typed content.</summary>
    Field,

    /// <summary>A group heading, painted in the accent colour.</summary>
    Section,

    Item,

    /// <summary>The chosen entry: drawn as a filled bar across the whole card.</summary>
    Selected,

    /// <summary>An entry that cannot be chosen.</summary>
    Disabled,

    Footer,
}

/// <summary>One card row: a role, the left label, and optional right-aligned text.</summary>
public sealed record CardRow(CardRowKind Kind, string Label, string? Right = null);

/// <summary>A selectable card entry, optionally filed under a group heading.</summary>
public sealed record CardItem(string Label, string? Right = null, bool Enabled = true, string? Group = null);

/// <summary>
/// A composed card row: the text is already padded to the card width, so a view only has to
/// paint the background behind it and place it. That keeps every card identical whichever
/// view draws it.
/// </summary>
public sealed record CardLine(string Text, CardRowKind Kind, int Width);

/// <summary>
/// A floating card: no border glyphs, the filled surface is the frame. Rows carry a role
/// instead of a colour, so this stays pure arithmetic that snapshot tests can pin without a
/// terminal and the Terminal.Gui view is the only place that decides how a role looks.
/// </summary>
public static class TuiCard
{
    public const int MinWidth = 34;
    public const int MaxWidth = 88;

    /// <summary>Blank cells kept on each side of a row so the text never touches the edge.</summary>
    public const int PadX = 2;

    public static int ComputeWidth(int screenWidth) => Math.Clamp(screenWidth - 8, MinWidth, MaxWidth);

    /// <summary>Cells a row may use once the side padding is out.</summary>
    public static int InnerWidth(int width) => Math.Max(1, width - PadX * 2);

    /// <summary>
    /// Composes the card at <paramref name="width"/> into rows already padded to that width.
    /// Nothing here paints: the caller knows which terminal surface the card sits on and
    /// supplies the colours, which is why only the role travels with the text.
    /// </summary>
    public static IReadOnlyList<CardLine> Compose(
        int width,
        string title,
        string hint,
        IReadOnlyList<CardRow> body,
        string? footer,
        string? footerRight = null)
    {
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        return Frame(title, hint, body, footer, footerRight).Select(row => Row(w, row)).ToList();
    }

    /// <summary>Composes a list card: the search field, the entries and the footer.</summary>
    public static IReadOnlyList<CardLine> ComposeList(
        int width,
        string title,
        string hint,
        IReadOnlyList<CardItem> items,
        int selected,
        int maxVisible,
        string? footer,
        string? search = null) =>
        Compose(
            width,
            title,
            hint,
            ListRows(items, selected, maxVisible, search),
            footer,
            // A clipped list says where the cursor sits; a short one needs no ratio.
            items.Count > maxVisible ? $"{Math.Clamp(selected, 0, items.Count - 1) + 1}/{items.Count}" : null);

    private static CardLine Row(int width, CardRow row)
    {
        if (row.Kind == CardRowKind.Rule)
        {
            // The header divider spans the card: a borderless card separates its heading
            // with a dim rule instead of a box glyph.
            return new CardLine(new string('─', width), row.Kind, width);
        }

        var inner = InnerWidth(width);
        var bullet = row.Kind switch
        {
            CardRowKind.Selected => "● ",
            CardRowKind.Item or CardRowKind.Disabled => "  ",
            _ => string.Empty,
        };
        var text = new string(' ', PadX) + bullet;
        var right = string.Empty;
        if (!string.IsNullOrEmpty(row.Right))
        {
            right = TuiText.Truncate(row.Right, Math.Max(0, inner - TuiText.VisibleWidth(text)));
        }

        var labelRoom = Math.Max(0, inner - TuiText.VisibleWidth(text) - TuiText.VisibleWidth(right));
        var label = row.Kind == CardRowKind.Field
            // An input field shows the end of its content: the caret must stay visible even
            // when the typed value overflows the card.
            ? TuiText.Tail(row.Label, labelRoom)
            : TuiText.Truncate(row.Label, labelRoom);
        text += TuiText.Pad(label, labelRoom);
        if (right.Length > 0)
        {
            text = TuiText.Pad(text, inner - TuiText.VisibleWidth(right)) + right;
        }

        return new CardLine(TuiText.Pad(text, width), row.Kind, width);
    }

    /// <summary>
    /// The whole card: a spacer, the titled row with its hint, the header rule, the body, a
    /// spacer and the footer. The caller supplies the body so a picker, an approval and a
    /// document share one frame.
    /// </summary>
    public static IReadOnlyList<CardRow> Frame(
        string title,
        string hint,
        IReadOnlyList<CardRow> body,
        string? footer,
        string? footerRight = null) =>
    [
        new(CardRowKind.Spacer, string.Empty),
        new(CardRowKind.Title, title, hint),
        new(CardRowKind.Rule, string.Empty),
        .. body,
        new(CardRowKind.Spacer, string.Empty),
        new(CardRowKind.Footer, footer ?? string.Empty, footerRight),
    ];

    /// <summary>
    /// Body rows for a list of entries: the search field, a heading whenever the group
    /// changes, then one row per entry. The visible window always holds the chosen entry.
    /// </summary>
    public static IReadOnlyList<CardRow> ListRows(
        IReadOnlyList<CardItem> items,
        int selected,
        int maxVisible,
        string? search = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var rows = new List<CardRow>();
        if (search is not null)
        {
            rows.Add(new CardRow(CardRowKind.Search, search));
        }

        if (items.Count == 0)
        {
            rows.Add(new CardRow(CardRowKind.Disabled, "no matches"));
            return rows;
        }

        var all = new List<CardRow>(items.Count + 4);
        string? group = null;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Group is { } name && name != group)
            {
                group = name;
                all.Add(new CardRow(CardRowKind.Section, name));
            }

            var kind = !items[i].Enabled
                ? CardRowKind.Disabled
                : i == selected ? CardRowKind.Selected : CardRowKind.Item;
            all.Add(new CardRow(kind, items[i].Label, items[i].Right));
        }

        var window = Window(all.Count, SelectedRow(all), Math.Max(1, maxVisible));
        rows.AddRange(all.GetRange(window.Start, window.End - window.Start));
        return rows;
    }

    /// <summary>
    /// The card width for a viewport and the number of entry rows that fit in a viewport
    /// height. Two spacers, the title, the header rule and the footer are the frame's own rows.
    /// </summary>
    public static (int Width, int VisibleRows) Fit(int screenWidth, int screenHeight)
    {
        var width = ComputeWidth(screenWidth);
        return (width, Math.Max(1, screenHeight - TuiCardFrameRows));
    }

    /// <summary>Rows the frame spends on the top spacer, title, rule, bottom spacer and footer.</summary>
    public const int TuiCardFrameRows = 5;

    private static int SelectedRow(IReadOnlyList<CardRow> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind == CardRowKind.Selected)
            {
                return i;
            }
        }

        return 0;
    }

    private static (int Start, int End) Window(int count, int selectedRow, int size)
    {
        if (count <= size)
        {
            return (0, count);
        }

        var start = Math.Clamp(selectedRow - size / 2, 0, count - size);
        return (start, start + size);
    }
}