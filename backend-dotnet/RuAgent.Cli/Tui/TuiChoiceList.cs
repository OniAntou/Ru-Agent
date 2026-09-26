namespace RuAgent.Cli.Tui;

/// <summary>One selectable entry: what it shows, and the value it hands back.</summary>
public sealed record ChoiceItem(string Label, string Detail, string Value, bool Enabled = true);

public enum ChoiceOutcome
{
    None,
    Moved,

    /// <summary>Enter (or the matching digit) picked the current item.</summary>
    Confirmed,

    /// <summary>Escape dismissed the list without picking anything.</summary>
    Cancelled,
}

/// <summary>
/// The one selection surface: approvals, the <c>/model</c> picker and the <c>/backend</c>
/// picker all navigate through this, so arrow keys, digits and Escape behave identically and
/// are tested once. Rendering goes through <see cref="SlashDropdown"/>.
/// </summary>
public sealed class ChoiceList
{
    private const int MaxFilterLength = 64;
    private readonly List<ChoiceItem> _all;
    private List<ChoiceItem> _items;
    private int _matchedCount;

    public ChoiceList(string title, IReadOnlyList<ChoiceItem> items, int selected = 0, string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("A choice list needs at least one item.", nameof(items));
        }

        Title = string.IsNullOrWhiteSpace(title) ? "select" : title.Trim();
        _all = [.. items];
        _items = [.. items];
        _matchedCount = _items.Count;
        Selected = FirstEnabled(Math.Clamp(selected, 0, _items.Count - 1));
        Footer = footer;
    }

    public string Title { get; }

    public IReadOnlyList<ChoiceItem> Items => _items;

    public string? Footer { get; }

    /// <summary>The substring typed so far; empty until the operator starts filtering.</summary>
    public string Filter { get; private set; } = string.Empty;

    /// <summary>How many rows exist before filtering, so the footer can report the ratio.</summary>
    public int TotalCount => _all.Count;

    /// <summary>How many rows the current filter matches. Zero renders one disabled notice row.</summary>
    public int MatchedCount => _matchedCount;

    public int Selected { get; set; }

    private int FirstEnabled(int index)
    {
        if (index >= 0 && index < _items.Count && _items[index].Enabled)
        {
            return index;
        }

        var first = _items.FindIndex(i => i.Enabled);
        return first >= 0 ? first : Math.Clamp(index, 0, _items.Count - 1);
    }

    /// <summary>
    /// Narrows the list to the rows whose label, detail or value contains <paramref name="value"/>.
    /// Only letters and the id separators filter: digits stay the numbered-pick shortcut.
    /// </summary>
    public bool TryAppendFilter(char value)
    {
        if (!char.IsLetter(value) && value is not ('-' or '_' or '.'))
        {
            return false;
        }

        if (Filter.Length >= MaxFilterLength)
        {
            return false;
        }

        ApplyFilter(Filter + value);
        return true;
    }

    /// <summary>Drops the last filter character. False when there is nothing to drop.</summary>
    public bool TryTrimFilter()
    {
        if (Filter.Length == 0)
        {
            return false;
        }

        ApplyFilter(Filter[..^1]);
        return true;
    }

    private void ApplyFilter(string query)
    {
        Filter = query;
        _matchedCount = query.Length == 0
            ? _all.Count
            : _all.Count(item => Matches(item, query));
        _items = query.Length == 0
            ? [.. _all]
            : [.. _all.Where(item => Matches(item, query))];

        if (_items.Count == 0)
        {
            _items = [new ChoiceItem($"no match for '{query}'", string.Empty, string.Empty, Enabled: false)];
            Selected = 0;
            return;
        }

        Selected = FirstEnabled(0);
    }

    private static bool Matches(ChoiceItem item, string query) =>
        item.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Value.Contains(query, StringComparison.OrdinalIgnoreCase);

    public ChoiceItem Current => _items[Math.Clamp(Selected, 0, _items.Count - 1)];

    /// <summary>Walk the list, wrapping at both ends and skipping disabled items.</summary>
    public void Move(int delta)
    {
        if (_items.Count == 0 || delta == 0)
        {
            return;
        }

        var step = delta > 0 ? 1 : -1;
        var totalSteps = Math.Abs(delta);

        for (var s = 0; s < totalSteps; s++)
        {
            var attempts = 0;
            var next = Selected;
            var found = false;
            while (attempts < _items.Count)
            {
                next = ((next + step) % _items.Count + _items.Count) % _items.Count;
                attempts++;
                if (_items[next].Enabled)
                {
                    Selected = next;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                break;
            }
        }
    }

    /// <summary>Jump to a 1-based digit if the list is long enough and the item is enabled. Returns false otherwise.</summary>
    public bool TrySelectDigit(char digit)
    {
        if (digit is < '1' or > '9')
        {
            return false;
        }

        var index = digit - '1';
        if (index >= _items.Count || !_items[index].Enabled)
        {
            return false;
        }

        Selected = index;
        return true;
    }

    /// <summary>
    /// Routes one keystroke. A digit both moves and confirms, matching the numbered prompts
    /// operators are used to; Enter confirms the highlighted row and Escape cancels.
    /// </summary>
    public ChoiceOutcome HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                Move(-1);
                return ChoiceOutcome.Moved;

            case ConsoleKey.DownArrow:
                Move(1);
                return ChoiceOutcome.Moved;

            case ConsoleKey.Enter:
                return Current.Enabled ? ChoiceOutcome.Confirmed : ChoiceOutcome.None;

            case ConsoleKey.Escape:
                return ChoiceOutcome.Cancelled;

            default:
                if (key.KeyChar == '\r' || key.KeyChar == '\n')
                {
                    return Current.Enabled ? ChoiceOutcome.Confirmed : ChoiceOutcome.None;
                }

                if (TrySelectDigit(key.KeyChar))
                {
                    return ChoiceOutcome.Confirmed;
                }

                return ChoiceOutcome.None;
        }
    }

    /// <summary>The framed list, one line per row, every line exactly <paramref name="width"/> cells.</summary>
    public IReadOnlyList<string> Render(int width, int maxVisible = 8)
    {
        var items = _items
            .Select(item => new DropdownItem(item.Label, item.Detail, null, item.Enabled))
            .ToList();

        var footer = Filter.Length > 0
            ? $"filter: {Filter} · {MatchedCount}/{TotalCount}"
            : Footer ?? "↑↓ · enter";
        return SlashDropdown.Compose(width, Title, items, Selected, maxVisible, footer);
    }
}
