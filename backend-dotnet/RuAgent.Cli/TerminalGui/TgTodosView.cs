using RuAgent.Cli.Tui;
using RuAgent.Protocol.Harness;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Read-only session checklist, rendered as a right rail or a one-line summary.</summary>
internal sealed class TgTodosView : View
{
    private readonly List<(string Text, TuiToken Tone)> _rows = [];
    private int _wrapWidth = -1;
    private int _offset;
    private int _completed;
    private IReadOnlyList<AgentTodoWire> _items = [];

    internal bool HasItems => _items.Count > 0;
    internal bool AllCompleted => HasItems && _completed == _items.Count;
    internal bool Docked { get; set; }
    internal string ProgressLabel => $"Todos {_completed}/{_items.Count}";
    internal event Action? DetailsRequested;

    internal TgTodosView()
    {
        Visible = false;
        CanFocus = false;
        SetScheme(TgPalette.Scheme(TuiToken.Ink, TgPalette.CardBgRgb()));
    }

    internal bool SetItems(IReadOnlyList<AgentTodoWire> items)
    {
        var safe = items.Select(item => item with
        {
            Content = ChatDisplayText.Sanitize(item.Content, AgentActivityWireValidator.MaximumTodoContentLength),
        }).ToArray();
        if (_items.SequenceEqual(safe)) return false;
        _items = safe;
        _completed = safe.Count(item => item.Status == AgentTodoStatuses.Completed);
        _wrapWidth = -1;
        SetNeedsDraw();
        return true;
    }

    internal string[] DocumentRows() => _items.Select(item => Mark(item.Status).Text + item.Content).ToArray();

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (!HasItems || Viewport.Width < 1 || Viewport.Height < 1) return true;
        var header = (AllCompleted ? "✓ " : "") + ProgressLabel;
        if (!Docked)
        {
            Paint(0, header + " ▾  ctrl+t", AllCompleted ? TuiToken.Ok : TuiToken.Bright);
            return true;
        }

        EnsureRows();
        var page = PageRows;
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _rows.Count - page));
        Paint(0, header + "  ▾", TuiToken.Bright);
        for (var row = 0; row < page; row++)
        {
            var index = row + _offset;
            var item = index < _rows.Count ? _rows[index] : (Text: "", Tone: TuiToken.Ink);
            Paint(row + 1, item.Text, item.Tone);
        }
        var footer = _rows.Count > page ? $"{_offset + 1}–{Math.Min(_offset + page, _rows.Count)}/{_rows.Count}  " : "";
        Paint(Viewport.Height - 1, footer + "ctrl+t details", TuiToken.Meta);
        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Position is not { } position) return false;
        if (position.Y == 0 && mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
        {
            DetailsRequested?.Invoke();
        }
        else if (Docked && (mouse.Flags.HasFlag(MouseFlags.WheeledUp) || mouse.Flags.HasFlag(MouseFlags.WheeledDown)))
        {
            EnsureRows();
            var step = mouse.Flags.HasFlag(MouseFlags.WheeledUp) ? -3 : 3;
            _offset = Math.Clamp(_offset + step, 0, Math.Max(0, _rows.Count - PageRows));
            SetNeedsDraw();
        }
        else return false;
        mouse.Handled = true;
        return true;
    }

    private int PageRows => Math.Max(0, Viewport.Height - 2);

    private void EnsureRows()
    {
        var width = Math.Max(1, Viewport.Width - 4);
        if (_wrapWidth == width) return;
        _wrapWidth = width;
        _rows.Clear();
        int? activeRow = null;
        foreach (var item in _items)
        {
            var mark = Mark(item.Status);
            if (item.Status == AgentTodoStatuses.InProgress) activeRow ??= _rows.Count;
            var lines = TuiText.Wrap(item.Content, width);
            _rows.Add((mark.Text + (lines.Count > 0 ? lines[0] : ""), mark.Tone));
            foreach (var line in lines.Skip(1)) _rows.Add(("  " + line, mark.Tone));
        }
        _offset = Math.Clamp((activeRow ?? 0) - PageRows / 3, 0, Math.Max(0, _rows.Count - PageRows));
    }

    private static (string Text, TuiToken Tone) Mark(string status) => status switch
    {
        AgentTodoStatuses.Completed => ("✓ ", TuiToken.Ok),
        AgentTodoStatuses.InProgress => ("▸ ", TuiToken.Brand),
        _ => ("○ ", TuiToken.Meta),
    };

    private void Paint(int row, string text, TuiToken tone)
    {
        var width = Viewport.Width;
        var content = width > 2 ? " " + TuiText.Truncate(text, width - 2) : TuiText.Truncate(text, width);
        Move(0, row);
        SetAttribute(TgPalette.Attr(tone, TgPalette.CardBgRgb()));
        AddStr(TuiText.Pad(content, width));
    }
}
