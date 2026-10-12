using System.Text;
using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Input;

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
    public const int MaxRetainedRows = 2000;
    public const int MaxRetainedCharacters = 200_000;
    public const int MaxRetainedLineCharacters = 16_000;
    private const string HistoryTruncatedText = "  · earlier transcript rows truncated ·";

    private readonly List<(string Text, TranscriptStyle Style)> _lines = [];
    private readonly List<(string Text, TranscriptStyle Style)> _wrapped = [];
    private (int AfterLine, List<(string Text, TranscriptStyle Style)> Hidden)? _collapsedBlock;
    private bool _expanded;
    private int _wrapWidth = -1;
    private int _tailRows = 1;
    private bool _tailDirty;
    private bool _historyTruncated;
    private int _scrollTop;
    private bool _followingTail = true;
    private int _retainedCharacters;

    /// <summary>True while the last collapsed block is revealed by <see cref="ToggleExpanded"/>.</summary>
    public bool IsExpanded => _expanded;

    /// <summary>True once at least one row exists; the landing overlay hides while this is true.</summary>
    public bool HasContent => _lines.Count > 0;

    public bool IsHistoryTruncated => _historyTruncated;
    public int RetainedRowCount => _lines.Count + (_collapsedBlock?.Hidden.Count ?? 0);
    public int RetainedCharacterCount => _retainedCharacters;
    public bool IsFollowingTail => _followingTail;
    public bool CanScroll { get { EnsureWrapped(); return _wrapped.Count > Math.Max(1, Viewport.Height); } }
    public bool HasUnseenOutput { get; private set; }
    public int ScrollPosition { get { EnsureWrapped(); return _followingTail ? Math.Max(0, _wrapped.Count - Viewport.Height) : _scrollTop; } }

    /// <summary>Raised on the 0-to-1 transition, so the window can hide its landing overlay.</summary>
    public event Action? FirstContentAppended;

    /// <summary>Raised when <see cref="Clear"/> empties the transcript, so the landing returns.</summary>
    public event Action? ContentCleared;

    public event Action<bool>? UnseenOutputChanged;
    public event Action<bool>? FollowingTailChanged;

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
        _tailDirty = false;
        _historyTruncated = false;
        _scrollTop = 0;
        SetFollowingTail(true);
        _retainedCharacters = 0;
        SetHasUnseenOutput(false);
        ContentCleared?.Invoke();
        SetNeedsDraw();
    }

    public void Append(string text, TranscriptStyle style)
    {
        EnsureWrapped();
        var wasEmpty = _lines.Count == 0;
        var bounded = BoundLine(text);
        _lines.Add((bounded, style));
        _retainedCharacters += CharacterCount(bounded);
        if (wasEmpty)
        {
            TuiDebug.Log("transcript", $"first append hides landing (len={text.Length})");
            FirstContentAppended?.Invoke();
        }

        if (_expanded)
        {
            // The revealed rows trail the wrap cache; rebuild it on the next draw.
            _wrapWidth = -1;
        }
        else if (_wrapWidth > 0)
        {
            _tailRows = AppendWrapped(_lines[^1].Text, style, _wrapWidth);
        }

        MarkOutputUnseen();
        EnforceRetention();

        SetNeedsDraw();
    }

    /// <summary>
    /// Appends a collapsed block: the summary row stays visible, the hidden rows join the
    /// transcript only while the view is expanded. The previous block is replaced; only the
    /// last collapsed tool/diff block answers <see cref="ToggleExpanded"/>.
    /// </summary>
    public void AppendCollapsed(string summary, IReadOnlyList<string> hidden, TranscriptStyle style)
    {
        var wasEmpty = _lines.Count == 0;
        var boundedSummary = BoundLine(summary);
        _lines.Add((boundedSummary, style));
        _retainedCharacters += CharacterCount(boundedSummary);
        if (wasEmpty)
        {
            FirstContentAppended?.Invoke();
        }

        if (_collapsedBlock is { } previousBlock)
        {
            _retainedCharacters -= previousBlock.Hidden.Sum(row => CharacterCount(row.Text));
        }

        var boundedHidden = new List<(string Text, TranscriptStyle Style)>();
        var usedCharacters = RetainedCharacterCount;
        var didDropRows = false;
        foreach (var line in hidden)
        {
            var remainingRows = MaxRetainedRows - _lines.Count - boundedHidden.Count;
            var remainingCharacters = MaxRetainedCharacters - usedCharacters;
            if (remainingRows <= 0 || remainingCharacters <= 0)
            {
                didDropRows = true;
                break;
            }

            var bounded = BoundLineParts(line);
            var characterCount = CharacterCount(bounded);
            if (characterCount > remainingCharacters)
            {
                bounded = BoundText(bounded, remainingCharacters);
                characterCount = CharacterCount(bounded);
                didDropRows = true;
            }

            boundedHidden.Add((bounded, style));
            usedCharacters += characterCount;
            _retainedCharacters += characterCount;
            if (didDropRows)
            {
                break;
            }
        }

        _collapsedBlock = (_lines.Count, boundedHidden);
        _expanded = false;
        _wrapWidth = -1;
        MarkOutputUnseen();
        if (didDropRows)
        {
            MarkHistoryTruncated();
        }

        EnforceRetention();
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
        if (text.Length == 0)
        {
            return;
        }

        if (_lines.Count == 0)
        {
            Append(text, TranscriptStyle.Prose);
            return;
        }

        var last = _lines[^1];
        var grown = BoundLineParts(last.Text, text);
        _lines[^1] = (grown, last.Style);
        _retainedCharacters += last.Text.Length + (long)text.Length <= MaxRetainedLineCharacters
            ? CharacterCount(text)
            : CharacterCount(grown) - CharacterCount(last.Text);
        MarkOutputUnseen();
        if (_expanded)
        {
            _wrapWidth = -1;
        }
        else if (_wrapWidth > 0)
        {
            // Many deltas can arrive before the next frame. Reflow once when drawn/scrolled.
            _tailDirty = true;
        }

        EnforceRetention();
        SetNeedsDraw();
    }

    internal void ReplaceTail(string? text, TranscriptStyle style)
    {
        if (_lines.Count == 0) return;
        var previous = _lines[^1];
        _retainedCharacters -= CharacterCount(previous.Text);
        if (text is null) _lines.RemoveAt(_lines.Count - 1);
        else
        {
            var bounded = BoundLineParts(string.Empty, text);
            _lines[^1] = (bounded, style);
            _retainedCharacters += CharacterCount(bounded);
        }
        if (text is null || _expanded) _wrapWidth = -1;
        else if (_wrapWidth > 0) _tailDirty = true;
        MarkOutputUnseen();
        EnforceRetention();
        SetNeedsDraw();
    }

    public void ScrollRows(int delta) => MoveViewport(delta);

    public void Page(int delta) => MoveViewport(delta * Math.Max(1, Viewport.Height - 1));

    public void ScrollToTop()
    {
        SetFollowingTail(false);
        _scrollTop = 0;
        SetNeedsDraw();
    }

    public void ScrollToTail()
    {
        SetFollowingTail(true);
        SetHasUnseenOutput(false);
        SetNeedsDraw();
    }

    private void SetFollowingTail(bool followingTail)
    {
        if (_followingTail == followingTail) return;
        _followingTail = followingTail;
        FollowingTailChanged?.Invoke(followingTail);
    }

    protected override bool OnMouseEvent(Mouse mouseEvent)
    {
        if (!mouseEvent.IsWheel || !CanScroll) return false;
        if (mouseEvent.Flags.HasFlag(MouseFlags.WheeledUp)) ScrollRows(-3);
        else if (mouseEvent.Flags.HasFlag(MouseFlags.WheeledDown)) ScrollRows(3);
        else return false;
        mouseEvent.Handled = true;
        return true;
    }

    private void MarkOutputUnseen()
    {
        if (!_followingTail)
        {
            SetHasUnseenOutput(true);
        }
    }

    private void SetHasUnseenOutput(bool hasUnseenOutput)
    {
        if (HasUnseenOutput == hasUnseenOutput)
        {
            return;
        }

        HasUnseenOutput = hasUnseenOutput;
        UnseenOutputChanged?.Invoke(hasUnseenOutput);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var height = Viewport.Height;
        if (width <= 0 || height <= 0)
        {
            return true;
        }

        EnsureWrapped();

        var maxTop = Math.Max(0, _wrapped.Count - height);
        var offset = _followingTail ? maxTop : Math.Clamp(_scrollTop, 0, maxTop);
        _scrollTop = offset;
        for (var i = 0; i < height && offset + i < _wrapped.Count; i++)
        {
            var (text, style) = _wrapped[offset + i];
            SetAttribute(StyleAttribute(style));
            Move(0, i);
            if (style == TranscriptStyle.User)
            {
                AddStr(new string(' ', width));
                Move(0, i);
            }
            AddStr(text);
        }

        return true;
    }

    private void RebuildWrapped(int width)
    {
        var oldTop = _followingTail ? Math.Max(0, _wrapped.Count - Math.Max(1, Viewport.Height)) : _scrollTop;
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
        _tailDirty = false;
        _scrollTop = Math.Clamp(oldTop, 0, Math.Max(0, _wrapped.Count - Math.Max(1, Viewport.Height)));
    }

    private void MoveViewport(int delta)
    {
        EnsureWrapped();
        if (_followingTail)
        {
            _scrollTop = Math.Max(0, _wrapped.Count - Math.Max(1, Viewport.Height));
            SetFollowingTail(false);
        }

        _scrollTop = Math.Clamp(_scrollTop + delta, 0, Math.Max(0, _wrapped.Count - Math.Max(1, Viewport.Height)));
        SetNeedsDraw();
    }

    private void EnsureWrapped()
    {
        var width = Viewport.Width;
        if (width <= 0) return;
        if (width != _wrapWidth)
        {
            RebuildWrapped(width);
        }
        else if (_tailDirty && _lines.Count > 0)
        {
            _wrapped.RemoveRange(_wrapped.Count - _tailRows, _tailRows);
            _tailRows = AppendWrapped(_lines[^1].Text, _lines[^1].Style, width);
            _tailDirty = false;
        }
    }

    private void EnforceRetention()
    {
        var exceedsCap = RetainedRowCount > MaxRetainedRows || RetainedCharacterCount > MaxRetainedCharacters;
        var didTrim = exceedsCap;
        if (exceedsCap)
        {
            MarkHistoryTruncated();
        }

        while (RetainedRowCount > MaxRetainedRows || RetainedCharacterCount > MaxRetainedCharacters)
        {
            if (_historyTruncated && _lines.Count > 2)
            {
                RemoveLine(1);
            }
            else if (_collapsedBlock is { Hidden.Count: > 0 } block)
            {
                _retainedCharacters -= CharacterCount(block.Hidden[^1].Text);
                block.Hidden.RemoveAt(block.Hidden.Count - 1);
                _collapsedBlock = block;
            }
            else if (_lines.Count > 1)
            {
                RemoveLine(_historyTruncated ? 1 : 0);
            }
            else
            {
                break;
            }
        }

        _collapsedBlock = _collapsedBlock is { } current && current.AfterLine > _lines.Count
            ? RemoveCollapsedBlock()
            : _collapsedBlock;
        if (didTrim)
        {
            _wrapWidth = -1;
        }
    }

    private void RemoveLine(int index)
    {
        if (!_followingTail && _wrapWidth > 0)
        {
            _scrollTop = Math.Max(0, _scrollTop - WrapLine(_lines[index].Text, _lines[index].Style, _wrapWidth).Count);
        }

        _retainedCharacters -= CharacterCount(_lines[index].Text);
        _lines.RemoveAt(index);
        if (_collapsedBlock is { } block && index < block.AfterLine)
        {
            _collapsedBlock = (block.AfterLine - 1, block.Hidden);
        }
    }

    private void MarkHistoryTruncated()
    {
        if (_historyTruncated)
        {
            return;
        }

        _historyTruncated = true;
        _lines.Insert(0, (HistoryTruncatedText, TranscriptStyle.Meta));
        _retainedCharacters += CharacterCount(HistoryTruncatedText);
        if (!_followingTail && _wrapWidth > 0)
        {
            _scrollTop += TuiText.HardWrapLine(HistoryTruncatedText, _wrapWidth).Count;
        }
        if (_collapsedBlock is { } block)
        {
            _collapsedBlock = (block.AfterLine + 1, block.Hidden);
        }
    }

    private (int AfterLine, List<(string Text, TranscriptStyle Style)> Hidden)? RemoveCollapsedBlock()
    {
        if (_collapsedBlock is { } block)
        {
            _retainedCharacters -= block.Hidden.Sum(row => CharacterCount(row.Text));
        }

        return null;
    }

    private static string BoundLine(string text) => BoundLineParts(text);

    private static string BoundLineParts(params string[] parts)
    {
        if (parts.Sum(part => (long)part.Length) <= MaxRetainedLineCharacters)
        {
            return string.Concat(parts);
        }

        var builder = new StringBuilder(MaxRetainedLineCharacters);
        var count = 0;
        var truncated = false;
        foreach (var part in parts)
        {
            foreach (var rune in part.EnumerateRunes())
            {
                if (count + rune.Utf16SequenceLength > MaxRetainedLineCharacters)
                {
                    truncated = true;
                    break;
                }

                builder.Append(rune);
                count += rune.Utf16SequenceLength;
            }

            if (truncated)
            {
                break;
            }
        }

        if (truncated)
        {
            var lastRune = builder.ToString().EnumerateRunes().Last();
            builder.Length -= lastRune.Utf16SequenceLength;
            builder.Append('…');
        }

        return builder.ToString();
    }

    private static string BoundText(string text, int maxCharacters)
    {
        var builder = new StringBuilder(Math.Max(0, maxCharacters));
        foreach (var rune in text.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > maxCharacters)
            {
                break;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }

    private static int CharacterCount(string text) => text.EnumerateRunes().Count();

    private int AppendWrapped(string text, TranscriptStyle style, int width)
    {
        var rows = WrapLine(text, style, width);
        foreach (var row in rows)
        {
            _wrapped.Add((row, style));
        }

        return rows.Count;
    }

    private static IReadOnlyList<string> WrapLine(string text, TranscriptStyle style, int width)
    {
        // Reflow prose on words, but retain exact columns in indented/code-gutter rows.
        return style == TranscriptStyle.Prose && text.Length > 0
            && !text.StartsWith(' ') && !text.Contains('│') && !text.Contains("  ", StringComparison.Ordinal)
                ? TuiText.Wrap(text, width)
                : TuiText.HardWrapLine(text, width);
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
