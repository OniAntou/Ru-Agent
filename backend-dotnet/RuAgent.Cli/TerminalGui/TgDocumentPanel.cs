using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// The floating read-only window over a command's output: the slash commands that only report
/// (/status, /help) open this instead
/// of scrolling their report into the transcript straight away. Rows are wrapped hard so the
/// column-aligned output a command prints keeps its shape, and the view owns the scroll offset
/// so the owner only has to say when it closes.
/// </summary>
public sealed class TgDocumentPanel : View
{
    private IReadOnlyList<string> _body = [];
    private string _title = "output";
    private int _offset;
    private int _wrapWidth = -1;
    private List<string> _rows = [];

    public TgDocumentPanel()
    {
        CanFocus = true;
        SetScheme(TgPalette.Scheme(TuiToken.Ink));
        KeyDown += OnKeyDown;
    }

    /// <summary>Raised when Enter or Escape closes the window; the content is not discarded.</summary>
    public event Action? Closed;

    /// <summary>The first body row on screen, for tests and for the scroll state.</summary>
    public int Offset => _offset;

    /// <summary>Body rows currently on screen.</summary>
    public int VisibleRows => _rows.Count;

    public void SetContent(string title, IReadOnlyList<string> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _title = string.IsNullOrWhiteSpace(title) ? "output" : title.Trim();
        _body = body;
        _offset = 0;
        _wrapWidth = -1;
        SetNeedsDraw();
    }

    public void ScrollBy(int delta) => ScrollTo(_offset + delta);

    public void ScrollTo(int offset)
    {
        _offset = TuiDocumentPanel.ClampOffset(offset, TotalRows(PageSize()), PageSize());
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (Viewport.Width <= 0 || Viewport.Height <= 0)
        {
            return true;
        }

        var width = Math.Clamp(Viewport.Width, TuiDocumentPanel.MinWidth, TuiCard.MaxWidth);
        var inner = TuiCard.InnerWidth(width);
        if (inner != _wrapWidth)
        {
            _rows = TuiText.HardWrap(_body, inner);
            _wrapWidth = inner;
            _offset = TuiDocumentPanel.ClampOffset(_offset, _rows.Count, PageSize());
        }

        var body = new List<CardRow>(_rows.Count);
        for (var i = _offset; i < Math.Min(_rows.Count, _offset + PageSize()); i++)
        {
            body.Add(new CardRow(CardRowKind.Item, _rows[i]));
        }

        var card = TuiCard.Compose(width, _title, "esc", body, DocumentFooter());
        var left = Math.Max(0, (Viewport.Width - width) / 2);
        var top = Math.Max(0, (Viewport.Height - card.Count) / 2);
        for (var i = 0; i < card.Count && top + i < Viewport.Height; i++)
        {
            SetAttribute(TgCard.Attribute(card[i].Kind));
            Move(left, top + i);
            AddStr(card[i].Text);
        }

        return true;
    }

    private string DocumentFooter()
    {
        var total = _rows.Count;
        if (total == 0)
        {
            return "esc close";
        }

        var position = $"{_offset + 1}-{Math.Min(_offset + PageSize(), total)}/{total}";
        return $"↑↓ scroll · esc close · {position}";
    }

    private int PageSize() => Math.Max(1, Viewport.Height - TuiCard.TuiCardFrameRows);

    private int TotalRows(int page) => Math.Max(page, _wrapWidth < 0 ? _body.Count : _rows.Count);

    private void OnKeyDown(object? sender, Key key)
    {
        if (key == Key.CursorUp)
        {
            ScrollBy(-1);
        }
        else if (key == Key.CursorDown)
        {
            ScrollBy(1);
        }
        else if (key == Key.PageUp)
        {
            ScrollBy(-PageSize());
        }
        else if (key == Key.PageDown)
        {
            ScrollBy(PageSize());
        }
        else if (key == Key.Home)
        {
            ScrollTo(0);
        }
        else if (key == Key.End)
        {
            ScrollTo(int.MaxValue);
        }
        else if (key == Key.Enter || key == Key.Esc)
        {
            key.Handled = true;
            Closed?.Invoke();
            return;
        }
        else
        {
            return;
        }

        key.Handled = true;
    }
}
