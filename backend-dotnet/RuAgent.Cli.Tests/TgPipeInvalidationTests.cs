using System.Drawing;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgPipeInvalidationTests
{
    [Theory]
    [InlineData("a | b ")]
    [InlineData("log-file | value ")]
    [InlineData("`a|b` | value ")]
    [InlineData("escaped \\| pipe ")]
    public void Ordinary_pipe_rows_do_not_rebuild_retained_history(string prefix)
    {
        using var view = CreateView();
        for (var i = 0; i < 1000; i++) view.Append($"history {i} " + new string('x', 60), TranscriptStyle.Prose);
        _ = view.CanScroll;
        var rebuilds = view.FullWrapRebuildCount;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 150; i++)
        {
            view.Append(prefix + i.ToString("D4") + new string('x', 60), TranscriptStyle.Prose);
            _ = view.CanScroll;
        }
        var used = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(rebuilds, view.FullWrapRebuildCount);
        Assert.True(used < 8 * 1024 * 1024, $"Pipe append allocated {used / 1048576d:F2} MiB.");
        Assert.Contains(prefix + "0149", view.Lines[^1].Text);
        AssertMatchesFullRebuild(view);
    }

    [Fact]
    public void Pipe_tail_replacements_and_chunks_keep_layout_incremental()
    {
        using var view = CreateView();
        for (var i = 0; i < 500; i++) view.Append("history " + i, TranscriptStyle.Prose);
        view.Append("a | b", TranscriptStyle.Prose); _ = view.CanScroll;
        var rebuilds = view.FullWrapRebuildCount;
        for (var i = 0; i < 150; i++)
        {
            view.ReplaceTail("log | item " + i, TranscriptStyle.Prose); _ = view.CanScroll;
            view.AppendChunk(" | suffix"); _ = view.CanScroll;
        }
        Assert.Equal(rebuilds, view.FullWrapRebuildCount);
        AssertMatchesFullRebuild(view);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(80)]
    public void Streaming_separator_and_partial_body_match_full_rebuild_at_each_stage(int width)
    {
        using var view = CreateView(width);
        view.Append("Name | Value", TranscriptStyle.Prose); AssertMatchesFullRebuild(view);
        view.Append("--- | -", TranscriptStyle.Prose); AssertMatchesFullRebuild(view);
        view.AppendChunk("--"); AssertMatchesFullRebuild(view);
        view.Append("apple", TranscriptStyle.Prose); AssertMatchesFullRebuild(view);
        view.AppendChunk(" | 1"); AssertMatchesFullRebuild(view);
        view.ReplaceTail("pear | 200000", TranscriptStyle.Prose); AssertMatchesFullRebuild(view);
        view.ReplaceTail("ordinary text", TranscriptStyle.Prose); AssertMatchesFullRebuild(view);
        view.ReplaceTail("grape | 3", TranscriptStyle.Prose); AssertMatchesFullRebuild(view);
        view.ReplaceTail("tool | log", TranscriptStyle.Tool); AssertMatchesFullRebuild(view);
        view.ReplaceTail(null, TranscriptStyle.Prose); AssertMatchesFullRebuild(view);
    }

    [Fact]
    public void Append_reflows_real_table_when_new_cell_changes_column_width()
    {
        using var view = CreateView();
        view.Append("Name | Count", TranscriptStyle.Prose);
        view.Append("--- | ---", TranscriptStyle.Prose); _ = view.CanScroll;
        var rebuilds = view.FullWrapRebuildCount;
        view.Append("a | 1", TranscriptStyle.Prose); _ = view.CanScroll;
        view.Append("a much longer name | 123456", TranscriptStyle.Prose); _ = view.CanScroll;
        Assert.True(view.FullWrapRebuildCount > rebuilds);
        Assert.Contains(view.RenderedRows, row => row.Text.Contains('┼'));
        Assert.Contains(view.RenderedRows, row => row.Text.Contains("a much longer name"));
        AssertMatchesFullRebuild(view);
    }

    [Theory]
    [InlineData("**---** | ---", true)]
    [InlineData("&#45;&#45;&#45; | ---", false)]
    [InlineData(@"\-\-\- | ---", true)]
    public void Divider_classification_keeps_existing_styled_escaped_and_entity_behavior(string divider, bool formsTable)
    {
        using var view = CreateView();
        view.Append("A | B", TranscriptStyle.Prose); view.Append(divider, TranscriptStyle.Prose);
        view.Append("x | y", TranscriptStyle.Prose);
        Assert.Equal(formsTable, view.RenderedRows.Any(row => row.Text.Contains('┼')));
        AssertMatchesFullRebuild(view);
    }

    [Fact]
    public void Body_row_cap_and_later_independent_table_keep_their_boundaries()
    {
        using var view = CreateView();
        view.Append("Name | Value", TranscriptStyle.Prose); view.Append("--- | ---", TranscriptStyle.Prose);
        for (var i = 0; i < 110; i++) { view.Append($"item {i} | {i}", TranscriptStyle.Prose); AssertMatchesFullRebuild(view); }
        view.Append("", TranscriptStyle.Prose);
        view.Append("Other | Header", TranscriptStyle.Prose); view.Append("--- | ---", TranscriptStyle.Prose);
        view.Append("new | data", TranscriptStyle.Prose); AssertMatchesFullRebuild(view);
    }

    [Fact]
    public void Non_prose_pipes_do_not_join_an_adjacent_table()
    {
        using var view = CreateView();
        view.Append("A | B", TranscriptStyle.Prose); view.Append("--- | ---", TranscriptStyle.Prose);
        view.Append("x | y", TranscriptStyle.Prose); _ = view.CanScroll;
        var rebuilds = view.FullWrapRebuildCount;
        view.Append("tool | log", TranscriptStyle.Tool); _ = view.CanScroll;
        view.Append("user | prompt", TranscriptStyle.User); _ = view.CanScroll;
        Assert.Equal(rebuilds, view.FullWrapRebuildCount);
        AssertMatchesFullRebuild(view);
    }

    [Fact]
    public void Resize_detached_scroll_and_expanded_details_remain_correct()
    {
        using var view = CreateView();
        for (var i = 0; i < 100; i++) view.Append("history | " + i, TranscriptStyle.Prose);
        view.AppendCollapsed("tool summary", ["detail | one", "detail two"], TranscriptStyle.Tool);
        view.ToggleExpanded(); _ = view.CanScroll; view.ScrollToTop();
        view.Append("later | line", TranscriptStyle.Prose); _ = view.CanScroll;
        Assert.Equal(0, view.ScrollPosition); Assert.False(view.IsFollowingTail);
        Assert.Equal(new[] { "detail | one", "detail two" }, view.CollapsedRows.Select(row => row.Text));
        AssertMatchesFullRebuild(view);
        view.Frame = new Rectangle(0, 0, 24, 25); AssertMatchesFullRebuild(view);
    }

    [Fact]
    public void Retention_clear_and_projection_removal_cannot_leave_stale_table_indices()
    {
        using var view = CreateView();
        view.Append("A | B", TranscriptStyle.Prose); view.Append("--- | ---", TranscriptStyle.Prose);
        view.Append("x | y", TranscriptStyle.Prose); _ = view.CanScroll;
        for (var i = 0; i < 2010; i++) view.Append("history | " + i, TranscriptStyle.Prose);
        Assert.True(view.IsHistoryTruncated); AssertMatchesFullRebuild(view);
        view.Clear();
        view.Append("A | B", TranscriptStyle.Prose); view.Append("--- | ---", TranscriptStyle.Prose);
        view.Append("x | y", TranscriptStyle.Prose); _ = view.CanScroll;
        view.ReplaceProjectedRows(2, [new("plain | replacement", TranscriptStyle.Prose)]);
        AssertMatchesFullRebuild(view);
    }

    private static TgTranscriptView CreateView(int width = 100) => new() { Frame = new Rectangle(0, 0, width, 25) };

    private static void AssertMatchesFullRebuild(TgTranscriptView view)
    {
        var incremental = Signature(view);
        var frame = view.Frame;
        view.Frame = new Rectangle(frame.X, frame.Y, frame.Width + 1, frame.Height); _ = view.CanScroll;
        view.Frame = frame;
        Assert.Equal(incremental, Signature(view));
    }

    private static string[] Signature(TgTranscriptView view) => view.RenderedRows.Select(row =>
        $"{row.Style}:{row.Text}:" + string.Join(";", row.Spans?.Select(span => $"{span.Text}:{span.Attr}") ?? [])).ToArray();
}
