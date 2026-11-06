using System.Globalization;
using System.Text;
using RuAgent.Cli.Tui;
using Terminal.Gui.Input;

namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgTranscriptView
{
    private readonly record struct SelectionPoint(int Row, int Column, TgTranscriptRow RenderedRow);
    private SelectionPoint? _textAnchor;
    private SelectionPoint? _textEnd;
    private bool _draggingText;

    internal bool HasTextSelection
    {
        get
        {
            if (_textEnd is null) return false;
            EnsureWrapped();
            return TryGetTextRange(out _, out _);
        }
    }

    private bool HandleTextSelection(Mouse mouse)
    {
        var pressed = mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed);
        var released = mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased);
        if ((!pressed && !released) || mouse.Position is not { } position) return false;
        if (SuperView is TgChatWindow { HasBlockingOverlay: true }) return false;

        var top = ScrollPosition;
        if (Viewport.Width <= 0 || Viewport.Height <= 0 || _wrapped.Count == 0) return false;
        var row = Math.Min(top + Math.Clamp(position.Y, 0, Viewport.Height - 1), _wrapped.Count - 1);
        var point = new SelectionPoint(row, Math.Clamp(position.X, 0, Viewport.Width - 1), _wrapped[row]);

        if (pressed && !mouse.Flags.HasFlag(MouseFlags.PositionReport))
        {
            ClearTextSelection();
            if (top + position.Y >= _wrapped.Count || App?.Mouse.IsGrabbed() == true) return false;
            _selectedBlock = null;
            _textAnchor = point;
            _draggingText = true;
            App?.Mouse.GrabMouse(this);
            return true;
        }

        if (!_draggingText || _textAnchor is not { } anchor) return false;
        if (!IsCurrent(anchor)) ClearTextSelection();
        else
        {
            _textEnd = point == anchor ? null : point;
            if (released) EndTextDrag();
            SetNeedsDraw();
        }
        return true;
    }

    internal void ClearTextSelection()
    {
        if (_textAnchor is null) return;
        EndTextDrag();
        _textAnchor = null;
        _textEnd = null;
        SetNeedsDraw();
    }

    private void EndTextDrag()
    {
        _draggingText = false;
        if (App?.Mouse.IsGrabbed(this) == true) App.Mouse.UngrabMouse();
    }

    private bool IsCurrent(SelectionPoint point) =>
        point.Row < _wrapped.Count && ReferenceEquals(point.RenderedRow, _wrapped[point.Row]);

    private bool TryGetTextRange(out SelectionPoint first, out SelectionPoint last)
    {
        first = last = default;
        if (_textAnchor is not { } anchor || _textEnd is not { } end
            || !IsCurrent(anchor) || !IsCurrent(end)) return false;
        (first, last) = (anchor.Row, anchor.Column).CompareTo((end.Row, end.Column)) < 0
            ? (anchor, end) : (end, anchor);
        return true;
    }

    private (int Column, string Text) SelectedSlice(int row, SelectionPoint first, SelectionPoint last)
    {
        var left = row == first.Row ? first.Column : 0;
        var right = row == last.Row ? last.Column : int.MaxValue;
        var text = _wrapped[row].Text;
        var start = -1;
        var startColumn = 0;
        var column = 0;
        var index = 0;
        while (index < text.Length && column <= right)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(index));
            var width = length == 1 && text[index] is >= ' ' and <= '~'
                ? 1 : TuiText.VisibleWidth(text.Substring(index, length));
            if (start < 0 && column + width > left)
            {
                start = index;
                startColumn = column;
            }
            column += width;
            index += length;
        }
        return start < 0 ? (0, string.Empty) : (startColumn, text[start..index]);
    }

    private string? CopyTextSelection()
    {
        if (_textEnd is null) return null;
        EnsureWrapped();
        if (!TryGetTextRange(out var first, out var last)) return null;
        var result = new StringBuilder();
        for (var row = first.Row; row <= last.Row; row++)
        {
            if (row > first.Row) result.AppendLine();
            result.Append(SelectedSlice(row, first, last).Text);
        }
        return result.ToString();
    }

    private void DrawTextSelection(int row, int y)
    {
        if (!TryGetTextRange(out var first, out var last) || row < first.Row || row > last.Row) return;
        var (column, text) = SelectedSlice(row, first, last);
        if (text.Length == 0) return;
        Move(column, y);
        SetAttribute(TgPalette.Attr(TgPalette.CardOnSelectionRgb(), TgPalette.CardSelectionRgb()));
        AddStr(text);
    }
}
