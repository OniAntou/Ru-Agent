namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgTranscriptView
{
    private sealed class CollapsedBlock(int afterLine, bool hasSummary)
    {
        public int AfterLine { get; set; } = afterLine;
        public bool HasSummary { get; } = hasSummary;
        public List<(string Text, TranscriptStyle Style)> Hidden { get; } = [];
        public bool Expanded { get; set; }
        public int WrappedTop { get; set; }
    }

    private readonly List<CollapsedBlock> _collapsedBlocks = [];
    private CollapsedBlock? _selectedBlock;

    /// <summary>True while the selected or latest collapsed block is revealed.</summary>
    public bool IsExpanded => SelectedOrLatestBlock?.Expanded == true;

    /// <summary>Test seam: the hidden rows of the selected or latest block.</summary>
    internal IReadOnlyList<(string Text, TranscriptStyle Style)> CollapsedRows =>
        SelectedOrLatestBlock?.Hidden ?? [];

    private bool HasExpandedBlocks => _collapsedBlocks.Any(block => block.Expanded);
    private CollapsedBlock? SelectedOrLatestBlock => _selectedBlock ?? _collapsedBlocks.LastOrDefault();

    /// <summary>
    /// Appends a summary and retains its hidden detail rows as an independent block.
    /// All rows count toward the transcript's existing retention budget.
    /// </summary>
    public void AppendCollapsed(string summary, IReadOnlyList<string> hidden, TranscriptStyle style)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(hidden);
        var wasEmpty = _lines.Count == 0;
        var boundedSummary = BoundLine(summary);
        _lines.Add((boundedSummary, style));
        _retainedCharacters += CharacterCount(boundedSummary);
        if (wasEmpty)
        {
            FirstContentAppended?.Invoke();
        }

        AddCollapsedBlock(hidden, style, hasSummary: true);
    }

    /// <summary>
    /// Replaces the current summaryless retained detail block, or creates one at the
    /// current transcript tail. Tool summaries use <see cref="AppendCollapsed"/> and
    /// remain independent of these live thinking details.
    /// </summary>
    internal void SetCollapsedDetails(IReadOnlyList<string> hidden, TranscriptStyle style)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        var previous = _collapsedBlocks.LastOrDefault(block =>
            !block.HasSummary && block.AfterLine == _lines.Count);
        var wasSelected = ReferenceEquals(previous, _selectedBlock);
        var wasExpanded = previous?.Expanded == true;
        if (previous is not null)
        {
            RemoveCollapsedBlock(previous);
        }

        if (hidden.Count == 0)
        {
            EnforceRetention();
            _wrapWidth = -1;
            SetNeedsDraw();
            return;
        }

        var added = AddCollapsedBlock(hidden, style, hasSummary: false, wasExpanded);
        if (wasSelected && _collapsedBlocks.Contains(added))
        {
            _selectedBlock = added;
        }
    }

    /// <summary>Selects and reveals a retained block while parking the viewport on it.</summary>
    public void MoveBlockSelection(int delta)
    {
        if (_collapsedBlocks.Count == 0)
        {
            _selectedBlock = null;
            return;
        }

        var currentIndex = _selectedBlock is null
            ? delta < 0 ? _collapsedBlocks.Count : delta > 0 ? -1 : _collapsedBlocks.Count - 1
            : _collapsedBlocks.IndexOf(_selectedBlock);
        var nextIndex = (int)Math.Clamp((long)currentIndex + delta, 0, _collapsedBlocks.Count - 1);
        _selectedBlock = _collapsedBlocks[nextIndex];
        _selectedBlock.Expanded = true;
        SetFollowingTail(false);
        _wrapWidth = -1;
        EnsureWrapped();
        SetNeedsDraw();
    }

    /// <summary>Clears explicit block selection so copy falls back to assistant text.</summary>
    public void ClearBlockSelection()
    {
        if (_selectedBlock is null)
        {
            return;
        }

        _selectedBlock = null;
        SetNeedsDraw();
    }

    /// <summary>
    /// Returns the selected block's retained detail rows, or the latest contiguous
    /// assistant prose/code rows when no block is explicitly selected.
    /// </summary>
    public string CopyText()
    {
        if (_selectedBlock is { } selected)
        {
            return string.Join(Environment.NewLine, selected.Hidden.Select(row => row.Text));
        }

        var end = _lines.FindLastIndex(row => row.Style == TranscriptStyle.Prose);
        if (end < 0)
        {
            return string.Empty;
        }

        var start = end;
        while (start > 0 && _lines[start - 1].Style == TranscriptStyle.Prose)
        {
            start--;
        }

        return string.Join(Environment.NewLine, _lines.Skip(start).Take(end - start + 1).Select(row => row.Text));
    }

    /// <summary>Toggles the selected block, defaulting to the newest retained block.</summary>
    public void ToggleExpanded()
    {
        var block = SelectedOrLatestBlock;
        if (block is null)
        {
            return;
        }

        block.Expanded = !block.Expanded;
        _wrapWidth = -1;
        SetNeedsDraw();
    }

    private CollapsedBlock AddCollapsedBlock(
        IReadOnlyList<string> hidden,
        TranscriptStyle style,
        bool hasSummary,
        bool expanded = false)
    {
        var block = new CollapsedBlock(_lines.Count, hasSummary) { Expanded = expanded };
        // Bound the incoming block itself, then let retention evict older history.
        // Accounting against the old transcript here would discard the newest tool details.
        var usedCharacters = hasSummary ? CharacterCount(_lines[^1].Text) : 0;
        var usedRows = hasSummary ? 1 : 0;
        var didDropRows = false;
        foreach (var line in hidden)
        {
            var remainingRows = MaxRetainedRows - usedRows - block.Hidden.Count;
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

            block.Hidden.Add((bounded, style));
            usedCharacters += characterCount;
            _retainedCharacters += characterCount;
            if (didDropRows)
            {
                break;
            }
        }

        if (block.Hidden.Count > 0 || hasSummary)
        {
            _collapsedBlocks.Add(block);
        }

        _wrapWidth = -1;
        MarkOutputUnseen();
        if (didDropRows)
        {
            MarkHistoryTruncated();
        }

        EnforceRetention();
        SetNeedsDraw();
        return block;
    }

    private void AppendCollapsedBlocksAfter(int afterLine, int? summaryTop, int width)
    {
        foreach (var block in _collapsedBlocks.Where(block => block.AfterLine == afterLine))
        {
            block.WrappedTop = block.HasSummary ? summaryTop ?? _wrapped.Count : _wrapped.Count;
            if (!block.Expanded)
            {
                continue;
            }

            foreach (var (hiddenText, hiddenStyle) in block.Hidden)
            {
                _tailRows = AppendWrapped(hiddenText, hiddenStyle, width);
            }
        }
    }

    private void RemoveBlocksAfter(int mark)
    {
        foreach (var block in _collapsedBlocks.Where(block => block.AfterLine > mark).ToArray())
        {
            RemoveCollapsedBlock(block);
        }
    }

    private void AdjustCollapsedBlocksForRemovedLine(int index)
    {
        foreach (var block in _collapsedBlocks.ToArray())
        {
            if (block.AfterLine == index + 1)
            {
                RemoveCollapsedBlock(block);
            }
            else if (block.AfterLine > index + 1)
            {
                block.AfterLine--;
            }
        }
    }

    private void RemoveCollapsedBlock(CollapsedBlock block)
    {
        if (!_collapsedBlocks.Remove(block))
        {
            return;
        }

        _retainedCharacters -= block.Hidden.Sum(row => CharacterCount(row.Text));
        if (ReferenceEquals(_selectedBlock, block))
        {
            _selectedBlock = null;
        }
    }

    private void ClearCollapsedBlocks()
    {
        _collapsedBlocks.Clear();
        _selectedBlock = null;
    }
}
