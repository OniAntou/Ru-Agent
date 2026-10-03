using System.Text;
using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Row styles the transcript can paint; maps 1:1 to the classic palette roles.</summary>
public enum TranscriptStyle
{
    Prose,
    User,
    Tool,
    Meta,
    Error,
}

/// <summary>
/// Append-only transcript with a tail pin and hard wrap — the Terminal.Gui analogue of the
/// classic renderer's sticky transcript region. Drawing stays custom so the OpenCode look
/// (user block, inline tool rows, diff indentation) survives the migration.
/// </summary>
public sealed class TgTranscriptView : View
{
    private readonly List<(string Text, TranscriptStyle Style)> _lines = [];
    private readonly List<(string Text, TranscriptStyle Style)> _wrapped = [];
    private (int AfterLine, List<(string Text, TranscriptStyle Style)> Hidden)? _collapsedBlock;
    private bool _expanded;
    private int _wrapWidth = -1;
    private int _tailRows = 1;

    /// <summary>True while the last collapsed block is revealed by <see cref="ToggleExpanded"/>.</summary>
    public bool IsExpanded => _expanded;

    /// <summary>Test seam: the committed rows in append order, before wrapping.</summary>
    internal IReadOnlyList<(string Text, TranscriptStyle Style)> Lines => _lines;

    /// <summary>Test seam: the hidden rows of the last collapsed block, expanded or not.</summary>
    internal IReadOnlyList<(string Text, TranscriptStyle Style)> CollapsedRows =>
        _collapsedBlock?.Hidden ?? [];

    /// <summary>
    /// Drops every row, wrap cache and collapsed block. `/clear` uses this so the TG
    /// transcript resets in place instead of accumulating the previous session's rows.
    /// </summary>
    public void Clear()
    {
        _lines.Clear();
        _wrapped.Clear();
        _collapsedBlock = null;
        _expanded = false;
        _wrapWidth = -1;
        _tailRows = 1;
        SetNeedsDraw();
    }

    public void Append(string text, TranscriptStyle style)
    {
        _lines.Add((text, style));
        if (_expanded)
        {
            // The revealed rows trail the wrap cache; rebuild it on the next draw.
            _wrapWidth = -1;
        }
        else if (_wrapWidth > 0)
        {
            _tailRows = AppendWrapped(text, style, _wrapWidth);
        }

        SetNeedsDraw();
    }

    /// <summary>
    /// Appends a collapsed block: the summary row stays visible, the hidden rows join the
    /// transcript only while the view is expanded. The previous block is replaced; only the
    /// last collapsed tool/diff block answers <see cref="ToggleExpanded"/>.
    /// </summary>
    public void AppendCollapsed(string summary, IReadOnlyList<string> hidden, TranscriptStyle style)
    {
        _lines.Add((summary, style));
        _collapsedBlock = (_lines.Count, hidden.Select(line => (line, style)).ToList());
        _expanded = false;
        _wrapWidth = -1;
        SetNeedsDraw();
    }

    /// <summary>Flips the last collapsed block; the next draw reveals or hides its rows.</summary>
    public void ToggleExpanded()
    {
        if (_collapsedBlock is null)
        {
            return;
        }

        _expanded = !_expanded;
        _wrapWidth = -1;
        SetNeedsDraw();
    }

    /// <summary>Appends to the tail line, the way streamed prose grows in place.</summary>
    public void AppendChunk(string text)
    {
        if (_lines.Count == 0)
        {
            Append(text, TranscriptStyle.Prose);
            return;
        }

        var last = _lines[^1];
        var grown = last.Text + text;
        _lines[^1] = (grown, last.Style);
        if (_expanded)
        {
            _wrapWidth = -1;
        }
        else if (_wrapWidth > 0)
        {
            // Re-wrap only the tail: its old rows are the last _tailRows of the cache.
            _wrapped.RemoveRange(_wrapped.Count - _tailRows, _tailRows);
            _tailRows = AppendWrapped(grown, last.Style, _wrapWidth);
        }

        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var height = Viewport.Height;
        if (width <= 0 || height <= 0)
        {
            return true;
        }

        if (width != _wrapWidth)
        {
            RebuildWrapped(width);
        }

        var offset = Math.Max(0, _wrapped.Count - height);
        for (var i = 0; i < height && offset + i < _wrapped.Count; i++)
        {
            var (text, style) = _wrapped[offset + i];
            SetAttribute(StyleAttribute(style));
            Move(0, i);
            AddStr(text);
        }

        return true;
    }

    private void RebuildWrapped(int width)
    {
        _wrapped.Clear();
        for (var i = 0; i < _lines.Count; i++)
        {
            var (text, style) = _lines[i];
            _tailRows = AppendWrapped(text, style, width);

            if (_expanded && _collapsedBlock is { } block && block.AfterLine == i + 1)
            {
                foreach (var (hiddenText, hiddenStyle) in block.Hidden)
                {
                    _tailRows = AppendWrapped(hiddenText, hiddenStyle, width);
                }
            }
        }

        _wrapWidth = width;
    }

    private int AppendWrapped(string text, TranscriptStyle style, int width)
    {
        var rows = HardWrapLine(text, width);
        foreach (var row in rows)
        {
            _wrapped.Add((row, style));
        }

        return rows.Count;
    }

    /// <summary>
    /// Splits one line into consecutive chunks of at most <paramref name="width"/> visible
    /// cells. Unlike <see cref="TuiText.Truncate"/> this never inserts an ellipsis or drops
    /// characters; an empty line stays one empty row.
    /// </summary>
    private static List<string> HardWrapLine(string text, int width)
    {
        var rows = new List<string>();
        if (text.Length == 0)
        {
            rows.Add(string.Empty);
            return rows;
        }

        var current = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeWidth = TuiText.RuneWidth(rune.Value);
            if (used + runeWidth > width && current.Length > 0)
            {
                rows.Add(current.ToString());
                current.Clear();
                used = 0;
            }

            current.Append(rune);
            used += runeWidth;
        }

        rows.Add(current.ToString());
        return rows;
    }

    private static Terminal.Gui.Drawing.Attribute StyleAttribute(TranscriptStyle style) => style switch
    {
        TranscriptStyle.User => TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()),
        TranscriptStyle.Tool => TgPalette.Attr(TuiToken.Meta),
        TranscriptStyle.Meta => TgPalette.Attr(TuiToken.Meta),
        TranscriptStyle.Error => TgPalette.Attr(TuiToken.Err),
        _ => TgPalette.Attr(TuiToken.Ink),
    };
}
