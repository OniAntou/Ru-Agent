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
/// One committed transcript row. <see cref="Text"/> is the stripped visible text every
/// consumer (classification, tables, bounds, copy) reads; <see cref="Spans"/>, when present,
/// paints that same text as per-run Terminal.Gui attributes for markdown-rendered rows and
/// always covers the whole text, so a row is either fully span-painted or plainly styled.
/// </summary>
internal sealed record TgTranscriptRow(string Text, TranscriptStyle Style, IReadOnlyList<TgSpan>? Spans = null);

/// <summary>
/// Append-only transcript with a tail pin and hard wrap — the Terminal.Gui analogue of the
/// classic renderer's sticky transcript region — plus one live activity row pinned after the
/// last committed row. Drawing stays custom so the OpenCode look (user block, inline tool
/// rows, diff indentation) survives the migration.
/// </summary>
public sealed partial class TgTranscriptView : View
{
    public const int MaxRetainedRows = 2000;
    public const int MaxRetainedCharacters = 200_000;
    public const int MaxRetainedLineCharacters = 16_000;
    private const string HistoryTruncatedText = "  · earlier transcript rows truncated ·";

    private readonly List<TgTranscriptRow> _lines = [];
    private readonly List<TgTranscriptRow> _wrapped = [];
    private int _wrapWidth = -1;
    private int _tailRows = 1;
    private bool _tailDirty;
    private bool _historyTruncated;
    internal long RetentionVersion { get; private set; }
    private int _scrollTop;
    private bool _followingTail = true;
    private int _retainedCharacters;
    private TuiActivityPresentation? _live;

    /// <summary>True once at least one row exists; the landing overlay hides while this is true.</summary>
    public bool HasContent => _lines.Count > 0;

    public bool IsHistoryTruncated => _historyTruncated;
    public int RetainedRowCount => _lines.Count + _collapsedBlocks.Sum(block => block.Hidden.Count);
    public int RetainedCharacterCount => _retainedCharacters;
    public bool IsFollowingTail => _followingTail;
    public bool CanScroll { get { EnsureWrapped(); return _wrapped.Count + LiveRows > Math.Max(1, Viewport.Height); } }
    public bool HasUnseenOutput { get; private set; }
    public int ScrollPosition { get { EnsureWrapped(); return _followingTail ? Math.Max(0, _wrapped.Count + LiveRows - Viewport.Height) : _scrollTop; } }

    /// <summary>Rows the live activity row occupies (one, or zero when idle).</summary>
    private int LiveRows => _live is null ? 0 : 1;

    /// <summary>Test seam: the current live activity row, null when none is shown.</summary>
    internal TuiActivityPresentation? Live => _live;

    /// <summary>Raised on the none-to-live and live-to-none transitions.</summary>
    public event Action<bool>? LiveActivityChanged;

    /// <summary>
    /// Sets or replaces the single live activity row pinned after the last committed row.
    /// The view draws it from the presentation's own spans; the caller owns the phase
    /// (spinner frame, tool glyph, elapsed) and clears it when the activity settles.
    /// </summary>
    public void UpdateLive(TuiActivityPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (_live == presentation)
        {
            return;
        }

        var hadLive = _live is not null;
        _live = presentation;
        if (!hadLive)
        {
            LiveActivityChanged?.Invoke(true);
        }

        SetNeedsDraw();
    }

    /// <summary>Drops the live activity row; a no-op when none is shown.</summary>
    public void ClearLive()
    {
        if (_live is null)
        {
            return;
        }

        _live = null;
        LiveActivityChanged?.Invoke(false);
        SetNeedsDraw();
    }

    /// <summary>Raised on the 0-to-1 transition, so the window can hide its landing overlay.</summary>
    public event Action? FirstContentAppended;

    /// <summary>Raised when <see cref="Clear"/> empties the transcript, so the landing returns.</summary>
    public event Action? ContentCleared;

    public event Action<bool>? UnseenOutputChanged;
    public event Action<bool>? FollowingTailChanged;

    /// <summary>Test seam: the committed rows in append order, before wrapping.</summary>
    internal IReadOnlyList<TgTranscriptRow> Lines => _lines;

    /// <summary>Rows committed so far; a step mark for <see cref="DropRowsSince"/>.</summary>
    internal int CommittedRowCount => _lines.Count;

    internal void ReplaceProseSince(int mark, IReadOnlyList<TgTranscriptRow> rows)
    {
        RetentionVersion++;
        for (var index = _lines.Count - 1; index >= mark; index--)
            if (_lines[index].Style == TranscriptStyle.Prose) RemoveLine(index);
        _wrapWidth = -1;
        foreach (var row in rows) Append(row.Text, TranscriptStyle.Prose, row.Spans);
        SetNeedsDraw();
    }

    internal void ReplaceToolRow(int index, string text)
    {
        if (index < 0 || index >= _lines.Count || _lines[index].Style != TranscriptStyle.Tool) return;
        if (_lines[index].Text.Contains("ctrl+o", StringComparison.Ordinal) && !text.Contains("ctrl+o", StringComparison.Ordinal))
            text += " · ctrl+o";
        var bounded = BoundLine(text);
        _retainedCharacters += CharacterCount(bounded) - CharacterCount(_lines[index].Text);
        _lines[index] = new TgTranscriptRow(bounded, TranscriptStyle.Tool);
        _wrapWidth = -1;
        EnforceRetention();
        SetNeedsDraw();
    }

    /// <summary>
    /// Drops every row appended since <paramref name="mark"/> (a
    /// <see cref="CommittedRowCount"/> reading). The host suppresses a dead-end reply
    /// before its recovery note, and a retried attempt supersedes the partial text it
    /// replaced, so the rows those streams painted leave the screen instead of stacking
    /// a duplicate answer under the one that replaced them. The wrap cache rebuilds on
    /// the next draw.
    /// </summary>
    internal void DropRowsSince(int mark)
    {
        if (mark < 0 || mark > _lines.Count)
        {
            return;
        }

        if (mark < _lines.Count) RetentionVersion++;
        for (var i = mark; i < _lines.Count; i++)
        {
            _retainedCharacters -= CharacterCount(_lines[i].Text);
        }

        _lines.RemoveRange(mark, _lines.Count - mark);
        RemoveBlocksAfter(mark);

        if (_historyTruncated && mark == 0)
        {
            // The truncation marker itself was dropped with everything after it.
            _historyTruncated = false;
        }

        _wrapWidth = -1;
        _tailDirty = false;
        _tailRows = 0;
        if (_lines.Count == 0)
        {
            ContentCleared?.Invoke();
        }

        SetNeedsDraw();
    }

    /// <summary>
    /// Drops every row, wrap cache and collapsed block. `/clear` uses this so the TG
    /// transcript resets in place instead of accumulating the previous session's rows.
    /// </summary>
    public void Clear()
    {
        RetentionVersion++;
        _lines.Clear();
        _wrapped.Clear();
        ClearCollapsedBlocks();
        _wrapWidth = -1;
        _tailRows = 1;
        _tailDirty = false;
        _historyTruncated = false;
        _scrollTop = 0;
        SetFollowingTail(true);
        _retainedCharacters = 0;
        _live = null;
        SetHasUnseenOutput(false);
        ContentCleared?.Invoke();
        SetNeedsDraw();
    }

    public void Append(string text, TranscriptStyle style) => Append(text, style, null);

    public void Append(string text, TranscriptStyle style, IReadOnlyList<TgSpan>? spans)
    {
        EnsureWrapped();
        var wasEmpty = _lines.Count == 0;
        var bounded = BoundLine(text);
        _lines.Add(new TgTranscriptRow(bounded, style, FitSpans(spans, text, bounded)));
        _retainedCharacters += CharacterCount(bounded);
        if (wasEmpty)
        {
            TuiDebug.Log("transcript", $"first append hides landing (len={text.Length})");
            FirstContentAppended?.Invoke();
        }

        if (HasExpandedBlocks || bounded.Contains('|'))
        {
            // The revealed rows trail the wrap cache; rebuild it on the next draw.
            _wrapWidth = -1;
        }
        else if (_wrapWidth > 0)
        {
            _tailRows = AppendWrapped(_lines[^1].Text, _lines[^1].Style, _lines[^1].Spans, _wrapWidth);
        }

        MarkOutputUnseen();
        EnforceRetention();

        SetNeedsDraw();
    }

    /// <summary>Appends to the tail line, the way streamed prose grows in place.</summary>
    public void AppendChunk(string text) => AppendChunk(text, null);

    /// <summary>
    /// Grows the tail row, optionally painting the new tail segment. When spans first
    /// reach a plain row the existing text becomes one ink span first, so the row keeps
    /// the spans-cover-the-whole-text invariant its drawing relies on.
    /// </summary>
    public void AppendChunk(string text, IReadOnlyList<TgSpan>? chunkSpans)
    {
        if (text.Length == 0 && chunkSpans is not { Count: > 0 })
        {
            return;
        }

        if (_lines.Count == 0)
        {
            Append(text, TranscriptStyle.Prose, chunkSpans);
            return;
        }

        var last = _lines[^1];
        var grown = BoundLineParts(last.Text, text);
        IReadOnlyList<TgSpan>? grownSpans;
        if (chunkSpans is { Count: > 0 })
        {
            var combined = new List<TgSpan>();
            if (last.Spans is { Count: > 0 } existing)
            {
                combined.AddRange(existing);
            }
            else
            {
                // The unspanned prefix is not part of any span yet, and spans must cover
                // everything the row draws.
                if (last.Text.Length > 0)
                {
                    combined.Add(new TgSpan(last.Text, StyleAttribute(last.Style)));
                }
            }

            combined.AddRange(chunkSpans);
            grownSpans = FitSpansToLength(combined, grown);
        }
        else
        {
            grownSpans = last.Spans;
        }

        _lines[^1] = new TgTranscriptRow(grown, last.Style, grownSpans);
        _retainedCharacters += last.Text.Length + (long)text.Length <= MaxRetainedLineCharacters
            ? CharacterCount(text)
            : CharacterCount(grown) - CharacterCount(last.Text);
        MarkOutputUnseen();
        if (HasExpandedBlocks)
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

    internal void ReplaceTail(string? text, TranscriptStyle style) => ReplaceTail(text, style, null);

    internal void ReplaceTail(string? text, TranscriptStyle style, IReadOnlyList<TgSpan>? spans)
    {
        if (_lines.Count == 0) return;
        var previous = _lines[^1];
        _retainedCharacters -= CharacterCount(previous.Text);
        if (text is null) _lines.RemoveAt(_lines.Count - 1);
        else
        {
            var bounded = BoundLineParts(string.Empty, text);
            _lines[^1] = new TgTranscriptRow(bounded, style, FitSpans(spans, text, bounded));
            _retainedCharacters += CharacterCount(bounded);
        }
        if (text is null || HasExpandedBlocks || text.Contains('|')) _wrapWidth = -1;
        else if (_wrapWidth > 0) _tailDirty = true;
        MarkOutputUnseen();
        EnforceRetention();
        SetNeedsDraw();
    }

    public void ScrollRows(int delta) => MoveViewport(delta);

    public void Page(int delta) => MoveViewport(delta * Math.Max(1, Viewport.Height - 1));

    public void ScrollToTop()
    {
        _selectedBlock = null;
        SetFollowingTail(false);
        _scrollTop = 0;
        SetNeedsDraw();
    }

    public void ScrollToTail()
    {
        _selectedBlock = null;
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

        var maxTop = Math.Max(0, _wrapped.Count + LiveRows - height);
        var offset = _followingTail ? maxTop : Math.Clamp(_scrollTop, 0, maxTop);
        _scrollTop = offset;
        for (var i = 0; i < height && offset + i < _wrapped.Count; i++)
        {
            DrawRow(i, _wrapped[offset + i], width);
        }

        // The live activity row is pinned after the last committed row: while the tail is
        // followed it stays visible at the bottom; scrolled up it sits below the viewport.
        if (_live is { } live)
        {
            var liveTop = _wrapped.Count - offset;
            if (liveTop >= 0 && liveTop < height)
            {
                Move(0, liveTop);
                foreach (var span in live.Fit(width))
                {
                    SetAttribute(TgPalette.Attr(span.Tone));
                    AddStr(span.Text);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// One committed row. Span-painted rows (markdown) draw run by run; the user block fills
    /// its row and paints the prompt chevron, tool rows split into glyph/label/rest with the
    /// outcome tone, everything else draws as one run in its style attribute.
    /// </summary>
    private void DrawRow(int row, TgTranscriptRow rowModel, int width)
    {
        var (text, style, spans) = rowModel;
        Move(0, row);
        if (spans is { Count: > 0 })
        {
            foreach (var span in spans)
            {
                SetAttribute(span.Attr);
                AddStr(span.Text);
            }

            return;
        }

        SetAttribute(StyleAttribute(style));
        if (style == TranscriptStyle.User)
        {
            AddStr(new string(' ', width));
            Move(0, row);
            if (text.Length > 0)
            {
                var prompt = text.IndexOf('❯');
                if (prompt >= 0)
                {
                    SetAttribute(TgPalette.Attr(TuiToken.Ink, TuiPalette.ElementRgb()));
                    AddStr(text[..prompt]);
                    // The green prompt chevron carries the accent the composer rail used to.
                    SetAttribute(TgPalette.Attr(TuiToken.Ok, TuiPalette.ElementRgb()));
                    AddStr("❯");
                    SetAttribute(TgPalette.Attr(TuiToken.Bright, TuiPalette.ElementRgb()));
                    AddStr(text[(prompt + 1)..]);
                }
                else
                {
                    SetAttribute(TgPalette.Attr(TuiToken.Bright, TuiPalette.ElementRgb()));
                    AddStr(text);
                }
            }

            return;
        }

        if (style == TranscriptStyle.Tool && text.Length > 2 && text[1] == ' ')
        {
            var tone = text[0] switch
            {
                '✗' => TuiToken.Err,
                '✓' => TuiToken.Ok,
                '·' => TuiToken.Meta,
                _ => TuiToken.Brand,
            };
            var labelEnd = text.IndexOf(' ', 2);
            if (labelEnd < 0) labelEnd = text.Length;
            SetAttribute(TgPalette.Attr(tone));
            AddStr(text[..1]);
            SetAttribute(TgPalette.Attr(tone == TuiToken.Err ? TuiToken.Err : TuiToken.Bright));
            AddStr(text[1..labelEnd]);
            SetAttribute(TgPalette.Attr(TuiToken.Meta));
            AddStr(text[labelEnd..]);
            return;
        }

        AddStr(text);
    }

    private void RebuildWrapped(int width)
    {
        var oldTop = _followingTail ? Math.Max(0, _wrapped.Count - Math.Max(1, Viewport.Height)) : _scrollTop;
        _wrapped.Clear();
        var rawRows = _lines.Select(row => row.Style == TranscriptStyle.Prose ? row.Text : string.Empty).ToArray();
        for (var i = 0; i < _lines.Count; i++)
        {
            var (text, style, spans) = _lines[i];
            if (style == TranscriptStyle.Prose
                && TuiMarkdownTable.TryRender(rawRows, i, Math.Min(width, 100), out var consumed, out var table))
            {
                _wrapped.AddRange(table.Select(row => new TgTranscriptRow(row, TranscriptStyle.Prose)));
                i += consumed - 1;
                _tailRows = 0;
                AppendCollapsedBlocksAfter(i + 1, summaryTop: null, width);
                continue;
            }
            var summaryTop = _wrapped.Count;
            _tailRows = AppendWrapped(text, style, spans, width);
            AppendCollapsedBlocksAfter(i + 1, summaryTop, width);
        }

        _wrapWidth = width;
        _tailDirty = false;
        var maxTop = Math.Max(0, _wrapped.Count - Math.Max(1, Viewport.Height));
        _scrollTop = _selectedBlock is { } selected
            ? Math.Clamp(selected.WrappedTop, 0, maxTop)
            : Math.Clamp(oldTop, 0, maxTop);
    }

    private void MoveViewport(int delta)
    {
        _selectedBlock = null;
        EnsureWrapped();
        if (_followingTail)
        {
            _scrollTop = Math.Max(0, _wrapped.Count + LiveRows - Math.Max(1, Viewport.Height));
            SetFollowingTail(false);
        }

        _scrollTop = Math.Clamp(_scrollTop + delta, 0, Math.Max(0, _wrapped.Count + LiveRows - Math.Max(1, Viewport.Height)));
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
            _tailRows = AppendWrapped(_lines[^1].Text, _lines[^1].Style, _lines[^1].Spans, width);
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
            else if (_collapsedBlocks.LastOrDefault(block => block.Hidden.Count > 0) is { } block)
            {
                _retainedCharacters -= CharacterCount(block.Hidden[^1].Text);
                block.Hidden.RemoveAt(block.Hidden.Count - 1);
                if (block.Hidden.Count == 0)
                {
                    RemoveCollapsedBlock(block);
                }
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

        RemoveBlocksAfter(_lines.Count);
        if (didTrim)
        {
            RetentionVersion++;
            _wrapWidth = -1;
        }
    }

    private void RemoveLine(int index)
    {
        if (!_followingTail && _wrapWidth > 0)
        {
            var removedRows = WrapLine(_lines[index].Text, _lines[index].Style, _lines[index].Spans, _wrapWidth).Count;
            foreach (var block in _collapsedBlocks.Where(block => block.AfterLine == index + 1 && block.Expanded))
            {
                removedRows += block.Hidden.Sum(row => WrapLine(row.Text, row.Style, null, _wrapWidth).Count);
            }

            _scrollTop = Math.Max(0, _scrollTop - removedRows);
        }

        _retainedCharacters -= CharacterCount(_lines[index].Text);
        _lines.RemoveAt(index);
        AdjustCollapsedBlocksForRemovedLine(index);
    }

    private void MarkHistoryTruncated()
    {
        if (_historyTruncated)
        {
            return;
        }

        _historyTruncated = true;
        _lines.Insert(0, new TgTranscriptRow(HistoryTruncatedText, TranscriptStyle.Meta));
        _retainedCharacters += CharacterCount(HistoryTruncatedText);
        if (!_followingTail && _wrapWidth > 0)
        {
            _scrollTop += TuiText.HardWrapLine(HistoryTruncatedText, _wrapWidth).Count;
        }
        foreach (var block in _collapsedBlocks)
        {
            block.AfterLine++;
        }
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

    private int AppendWrapped(string text, TranscriptStyle style, IReadOnlyList<TgSpan>? spans, int width)
    {
        if (spans is { Count: > 0 })
        {
            var rows = proseLikeForSpans(text, style)
                ? TgAnsiPaint.WordWrapSpans(text, spans, width)
                : TgAnsiPaint.HardWrapSpans(text, spans, width);
            foreach (var wrapped in rows)
            {
                _wrapped.Add(new TgTranscriptRow(wrapped.Row, style, wrapped.Spans));
            }

            return rows.Count;
        }

        var plain = WrapLine(text, style, null, width);
        foreach (var row in plain)
        {
            _wrapped.Add(new TgTranscriptRow(row, style, null));
        }

        return plain.Count;
    }

    private static IReadOnlyList<string> WrapLine(string text, TranscriptStyle style, IReadOnlyList<TgSpan>? spans, int width)
    {
        // Reflow prose on words across the full viewport — the classic renderer wraps at
        // the same width, and a capped measure left wide terminals with a half-width
        // column and words breaking mid-sentence. Indented/code-gutter rows still retain
        // their exact columns. Span-painted rows must slice their spans, so they route
        // through the span-aware wrappers in AppendWrapped instead of these helpers.
        return style == TranscriptStyle.Prose && text.Length > 0
            && !text.StartsWith(' ') && !text.Contains('│') && !text.Contains("  ", StringComparison.Ordinal)
                ? TuiText.Wrap(text, width)
                : TuiText.HardWrapLine(text, width);
    }

    private static bool proseLikeForSpans(string text, TranscriptStyle style) =>
        style == TranscriptStyle.Prose && text.Length > 0
        && !text.StartsWith(' ') && !text.Contains('│') && !text.Contains("  ", StringComparison.Ordinal);

    /// <summary>
    /// Aligns spans parsed from <paramref name="source"/> onto the bounded text actually
    /// stored. <see cref="BoundLine(string)"/> may have cut the tail runes or appended an
    /// ellipsis, so the result is clipped to the bounded length and any covering gap is
    /// padded with an ink span — a row either paints its whole text through spans or none.
    /// </summary>
    private static IReadOnlyList<TgSpan>? FitSpans(IReadOnlyList<TgSpan>? spans, string source, string bounded)
    {
        if (spans is not { Count: > 0 })
        {
            return null;
        }

        return FitSpansToLength(spans, bounded);
    }

    private static IReadOnlyList<TgSpan>? FitSpansToLength(IReadOnlyList<TgSpan> spans, string bounded)
    {
        var total = 0;
        foreach (var span in spans)
        {
            total += span.Text.Length;
        }

        if (total == 0)
        {
            return null;
        }

        var length = bounded.Length;
        if (total == length)
        {
            return spans;
        }

        var fitted = new List<TgSpan>(spans.Count + 1);
        var used = 0;
        foreach (var span in spans)
        {
            if (used >= length)
            {
                break;
            }

            var take = Math.Min(length - used, span.Text.Length);
            fitted.Add(span with { Text = span.Text[..take] });
            used += take;
        }

        if (used < length)
        {
            // The bounder replaced dropped runes with an ellipsis: cover it in ink so the
            // row still paints its whole text.
            fitted.Add(new TgSpan(bounded[used..], TgPalette.Attr(TuiToken.Ink)));
        }

        return fitted.Count == 0 ? null : fitted;
    }

    private static Terminal.Gui.Drawing.Attribute StyleAttribute(TranscriptStyle style) => style switch
    {
        TranscriptStyle.User => TgPalette.Attr(TuiToken.Ink, TuiPalette.ElementRgb()),
        TranscriptStyle.Tool => TgPalette.Attr(TuiToken.Meta),
        TranscriptStyle.Meta => TgPalette.Attr(TuiToken.Meta),
        TranscriptStyle.Error => TgPalette.Attr(TuiToken.Err),
        _ => TgPalette.Attr(TuiToken.Ink),
    };
}
