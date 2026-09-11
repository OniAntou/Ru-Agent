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
    private readonly List<ChoiceItem> _items;

    public ChoiceList(string title, IReadOnlyList<ChoiceItem> items, int selected = 0, string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("A choice list needs at least one item.", nameof(items));
        }

        Title = string.IsNullOrWhiteSpace(title) ? "select" : title.Trim();
        _items = [.. items];
        var clamped = Math.Clamp(selected, 0, _items.Count - 1);
        if (!_items[clamped].Enabled)
        {
            var firstEnabled = _items.FindIndex(i => i.Enabled);
            clamped = firstEnabled >= 0 ? firstEnabled : clamped;
        }
        Selected = clamped;
        Footer = footer;
    }

    public string Title { get; }

    public IReadOnlyList<ChoiceItem> Items => _items;

    public string? Footer { get; }

    public int Selected { get; set; }

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

        return SlashDropdown.Compose(width, Title, items, Selected, maxVisible, Footer ?? "↑↓ · enter");
    }
}
