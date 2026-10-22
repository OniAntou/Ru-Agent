using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The append-only sink (the fallback used when no terminal is available). It prints rows
/// and keeps a bounded history; it never positions the cursor, repaints or rebuilds.
/// </summary>
public sealed class TuiSurfacePlainTests
{
    private static (TuiSurface Surface, StringWriter Writer) Create()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var writer = new StringWriter();
        return (new TuiSurface(writer), writer);
    }

    [Fact]
    public void WriteLine_records_the_row_and_keeps_it_in_the_history()
    {
        var (surface, writer) = Create();

        surface.WriteLine("hello");

        Assert.Equal("hello" + Environment.NewLine, writer.ToString());
        Assert.Equal(1, surface.HistoryCount);
        Assert.Equal(["hello"], surface.SnapshotTail(1, 40));
    }

    [Fact]
    public void WriteLine_splits_embedded_newlines_into_separate_rows()
    {
        var (surface, _) = Create();

        surface.WriteLine("a\nb\r\nc");

        Assert.Equal(3, surface.HistoryCount);
        Assert.Equal(["a", "b", "c"], surface.SnapshotTail(3, 40));
    }

    [Fact]
    public void Line_completes_the_pending_fragment()
    {
        var (surface, _) = Create();

        surface.Write("partial");
        surface.Line();

        Assert.Equal(["partial"], surface.SnapshotTail(1, 40));
    }

    [Fact]
    public void Write_keeps_partial_text_pending_until_a_newline_arrives()
    {
        var (surface, _) = Create();

        surface.Write("no newline yet");

        Assert.Equal(0, surface.HistoryCount);
    }

    [Fact]
    public void WriteBlock_writes_every_row_and_flushes()
    {
        var (surface, writer) = Create();

        surface.WriteBlock(["one", "two"]);

        Assert.Equal(["one", "two"], surface.SnapshotTail(2, 40));
        Assert.Equal("one" + Environment.NewLine + "two" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void History_is_bounded_by_rows()
    {
        var (surface, _) = Create();

        for (var i = 0; i < TuiSurface.HistoryCapacity + 5; i++)
        {
            surface.WriteLine("row " + i);
        }

        Assert.Equal(TuiSurface.HistoryCapacity, surface.HistoryCount);
        Assert.Equal("row " + (TuiSurface.HistoryCapacity + 4), surface.SnapshotTail(1, 40)[0]);
    }

    [Fact]
    public void One_row_is_capped_at_the_documented_unit_limit()
    {
        var (surface, _) = Create();

        surface.WriteLine(new string('x', TuiSurface.MaxLineCharacters + 100));

        Assert.Equal(TuiSurface.MaxLineCharacters, surface.SnapshotTail(1, int.MaxValue)[0].Length);
    }

    [Fact]
    public void SnapshotTail_clips_each_row_to_the_requested_width()
    {
        var (surface, _) = Create();
        surface.WriteLine("abcdefghij");

        var rows = surface.SnapshotTail(1, 5);

        Assert.Equal(5, TuiText.VisibleWidth(rows[0]));
    }
}
