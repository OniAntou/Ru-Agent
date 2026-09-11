using System.Text;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiSurfaceTests
{
    [Fact]
    public void SnapshotTail_ReturnsRecordedLinesOldestFirst()
    {
        var surface = new TuiSurface(new StringWriter()) { SynchronizedOutput = false };

        surface.Write("first\nsecond\n");
        surface.WriteLine("third");

        Assert.Equal(["first", "second", "third"], surface.SnapshotTail(10, 80));
    }

    [Fact]
    public void WriteLine_WithMultilineText_SplitsIntoSeparateHistoryEntries()
    {
        var surface = new TuiSurface(new StringWriter()) { SynchronizedOutput = false };
        surface.WriteLine("card_top\ncard_body\ncard_bottom");

        Assert.Equal(["card_top", "card_body", "card_bottom"], surface.SnapshotTail(10, 80));
    }

    [Fact]
    public void WriteLineVolatile_StaysOutOfTheRebuildHistory()
    {
        var surface = new TuiSurface(new StringWriter()) { SynchronizedOutput = false };

        surface.WriteLine("kept");
        surface.WriteLineVolatile("reprint");

        Assert.Equal(["kept"], surface.SnapshotTail(10, 80));
    }

    [Fact]
    public void RepeatedRebuilds_DoNotGrowHistoryWithReprints()
    {
        // Simulates RedrawScreenForResize: volatile header reprint plus a raw
        // tail replay must never feed copies back into the rebuild history,
        // or every resize would print more headers than the last one.
        var writer = new StringWriter();
        var surface = new TuiSurface(writer) { SynchronizedOutput = false };

        surface.WriteLine("◆ 19:46");
        surface.WriteLine("Hello!");

        for (var i = 0; i < 5; i++)
        {
            surface.WriteLineVolatile("✦ Ru · coding agent");
            foreach (var line in surface.SnapshotTail(100, 80))
            {
                writer.WriteLine(line);
            }
        }

        Assert.Equal(["◆ 19:46", "Hello!"], surface.SnapshotTail(1000, 80));
    }

    [Fact]
    public void SnapshotTail_ReturnsOnlyTheRequestedTail()
    {
        var surface = new TuiSurface(new StringWriter()) { SynchronizedOutput = false };
        for (var i = 1; i <= 10; i++)
        {
            surface.WriteLine($"line {i}");
        }

        Assert.Equal(["line 8", "line 9", "line 10"], surface.SnapshotTail(3, 80));
    }

    [Fact]
    public void SnapshotTail_ClipsLinesToTheGivenWidth()
    {
        var surface = new TuiSurface(new StringWriter()) { SynchronizedOutput = false };
        surface.WriteLine(new string('x', 100));

        var line = Assert.Single(surface.SnapshotTail(5, 20));
        Assert.Equal(20, TuiText.VisibleWidth(line));
    }

    [Fact]
    public void SnapshotTail_DoesNotRecordMutableRepaints()
    {
        var surface = new TuiSurface(new StringWriter()) { SynchronizedOutput = false };

        surface.Repaint(w => w.Write("live row"));
        surface.RepaintSingleLine("live row", 40);

        Assert.Empty(surface.SnapshotTail(10, 80));
    }

    [Fact]
    public void SnapshotTail_KeepsAPartialLineSeparate()
    {
        var surface = new TuiSurface(new StringWriter()) { SynchronizedOutput = false };

        surface.Write("no newline yet");

        Assert.Empty(surface.SnapshotTail(10, 80));

        surface.Line();
        Assert.Equal(["no newline yet"], surface.SnapshotTail(10, 80));
    }

    [Fact]
    public void SnapshotTail_IsBounded()
    {
        var surface = new TuiSurface(new StringWriter()) { SynchronizedOutput = false };
        for (var i = 0; i < TuiSurface.HistoryCapacity + 50; i++)
        {
            surface.WriteLine($"line {i}");
        }

        var tail = surface.SnapshotTail(TuiSurface.HistoryCapacity + 100, 80);

        Assert.Equal(TuiSurface.HistoryCapacity, tail.Count);
        Assert.Equal($"line {TuiSurface.HistoryCapacity + 49}", tail[^1]);
    }

    [Fact]
    public void SnapshotTail_WithNoRoom_ReturnsNothing()
    {
        var surface = new TuiSurface(new StringWriter()) { SynchronizedOutput = false };
        surface.WriteLine("line");

        Assert.Empty(surface.SnapshotTail(0, 80));
    }

    [Fact]
    public void Repaint_WrapsInSynchronizedOutput()
    {
        var writer = new StringWriter();
        var surface = new TuiSurface(writer) { SynchronizedOutput = true };

        surface.Repaint(w => w.Write("row"));

        Assert.Equal("\u001b[?2026hrow\u001b[?2026l", writer.ToString());
    }

    [Fact]
    public void Repaint_SkipsSynchronizedOutputWhenDisabled()
    {
        var writer = new StringWriter();
        var surface = new TuiSurface(writer) { SynchronizedOutput = false };

        surface.Repaint(w => w.Write("row"));

        Assert.Equal("row", writer.ToString());
    }

    [Fact]
    public void RepaintSingleLine_ClearsThenClipsToWidth()
    {
        var writer = new StringWriter();
        var surface = new TuiSurface(writer) { SynchronizedOutput = false };

        surface.RepaintSingleLine("0123456789", 5);

        var text = writer.ToString();
        Assert.StartsWith("\r\u001b[2K", text, StringComparison.Ordinal);
        Assert.Contains("0123…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RepaintSingleLine_NeverSplitsAnsiSequences()
    {
        var writer = new StringWriter();
        var surface = new TuiSurface(writer) { SynchronizedOutput = false };
        var styled = TuiPalette.Paint("0123456789", TuiToken.Brand);

        surface.RepaintSingleLine(styled, 4);

        var visible = TuiText.Strip(writer.ToString().Replace("\r", string.Empty));
        Assert.Equal("012…", visible);
    }

    [Fact]
    public void ClearSingleLine_EmitsCarriageReturnAndErase()
    {
        var writer = new StringWriter();
        var surface = new TuiSurface(writer) { SynchronizedOutput = false };

        surface.ClearSingleLine(40);

        Assert.Equal("\r\u001b[2K", writer.ToString());
    }

    [Fact]
    public async Task WriteAndRepaint_FromConcurrentThreads_NeverTearARow()
    {
        var writer = new RowRecordingWriter();
        var surface = new TuiSurface(writer) { SynchronizedOutput = false };

        var transcripts = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                surface.WriteLine("TRANSCRIPT-LINE");
            }
        });
        var rows = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                surface.RepaintSingleLine("LIVEROW", 40);
            }
        });

        await Task.WhenAll(transcripts, rows);

        Assert.NotEmpty(writer.Rows);
        Assert.All(writer.Rows, row =>
            Assert.False(
                row.Contains("TRANSCRIPT-LINE", StringComparison.Ordinal)
                    && row.Contains("LIVEROW", StringComparison.Ordinal),
                $"torn row: '{row}'"));
    }

    /// <summary>Records one entry per terminal row: newline for transcript, carriage return for repaints.</summary>
    private sealed class RowRecordingWriter : TextWriter
    {
        private readonly StringBuilder _current = new();
        private readonly List<string> _rows = new();

        public IReadOnlyList<string> Rows => _rows;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value is '\n' or '\r')
            {
                if (_current.Length > 0)
                {
                    _rows.Add(_current.ToString());
                    _current.Clear();
                }

                return;
            }

            _current.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (var character in value)
            {
                Write(character);
            }
        }
    }
}
