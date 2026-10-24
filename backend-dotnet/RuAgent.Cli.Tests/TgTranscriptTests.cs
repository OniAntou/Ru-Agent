using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgTranscriptTests
{
    [Fact]
    public void StreamingBurstReflowsOnceWithoutAllocatingGigabytes()
    {
        using var transcript = new TgTranscriptView { Frame = new System.Drawing.Rectangle(0, 0, 80, 10) };
        transcript.Append("", TranscriptStyle.Prose);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 4000; i++) transcript.AppendChunk("word");
        Assert.True(transcript.CanScroll);
        Assert.Equal(16000, transcript.RetainedCharacterCount);
        Assert.Equal(string.Concat(Enumerable.Repeat("word", 4000)), Assert.Single(transcript.Lines).Text);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 96L * 1024 * 1024);
    }

    [Fact]
    public void ProseWrapsOnWordsWhileAlignedRowsRetainTheirSpaces()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var transcript = new TgTranscriptView { Width = 16, Height = Dim.Fill() };
        window.Add(transcript);
        transcript.Append("alpha beta gamma delta epsilon", TranscriptStyle.Prose);
        transcript.Append("  a    b", TranscriptStyle.Prose);
        transcript.Append("Name    Value", TranscriptStyle.Prose);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Equal("alpha beta gamma", rows[0]);
            Assert.Equal("delta epsilon", rows[1]);
            Assert.Equal("  a    b", rows[2]);
            Assert.Equal("Name    Value", rows[3]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void WrappedBulletPresentationRepeatsItsContinuationIndent()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var transcript = new TgTranscriptView { Width = 30, Height = Dim.Fill() };
        window.Add(transcript);
        var line = new TuiMarkdown().RenderPresentation(
            "- " + string.Join(' ', Enumerable.Repeat("word", 20)), 30)[0];
        var spans = line.Runs
            .Select(run => new TgSpan(run.Text, TgPalette.Attr(run.Token)))
            .ToArray();
        transcript.Append(line.Text, TranscriptStyle.Prose, spans);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = TgSnapshot.Capture(app).TakeWhile(row => row.Length > 0).ToList();
            Assert.True(rows.Count > 1);
            Assert.StartsWith("  •  ", rows[0], StringComparison.Ordinal);
            Assert.All(rows.Skip(1), row => Assert.StartsWith("     ", row, StringComparison.Ordinal));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void ProseWrapsAtTheFullViewportWidthOnAWideScreen()
    {
        // The transcript used to cap prose at 100 cells, so a 160-column frame wrapped
        // mid-sentence at 100 while the classic renderer used the full width. One
        // 151-character line must stay on one row here.
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var transcript = new TgTranscriptView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(transcript);
        transcript.Append(new string('a', 100) + " " + new string('b', 50), TranscriptStyle.Prose);
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            app.Driver!.SetScreenSize(160, 12);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Contains(new string('b', 50), rows[0]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void DropRowsSince_RemovesOnlyTheSupersededTail()
    {
        using var transcript = new TgTranscriptView { Frame = new System.Drawing.Rectangle(0, 0, 80, 10) };
        transcript.Append("kept", TranscriptStyle.Prose);
        var mark = transcript.CommittedRowCount;
        transcript.Append("suppressed one", TranscriptStyle.Prose);
        transcript.Append("suppressed two", TranscriptStyle.Prose);

        transcript.DropRowsSince(mark);

        var kept = Assert.Single(transcript.Lines);
        Assert.Equal("kept", kept.Text);
        // The retention counters follow the drop, so later trims still measure the view.
        Assert.Equal(4, transcript.RetainedCharacterCount);
    }

    [Fact]
    public void RemoveProseSince_KeepsSettledToolRows()
    {
        using var transcript = new TgTranscriptView { Frame = new System.Drawing.Rectangle(0, 0, 80, 10) };
        transcript.Append("kept", TranscriptStyle.Prose);
        var mark = transcript.CommittedRowCount;
        transcript.Append("✓ Read a.cs", TranscriptStyle.Tool);
        transcript.Append("superseded", TranscriptStyle.Prose);

        transcript.RemoveProseSince(mark);

        Assert.Equal(["kept", "✓ Read a.cs"], transcript.Lines.Select(row => row.Text));
    }

    [Fact]
    public void DropRowsSince_ErasesTheDroppedRowsFromTheScreen()
    {
        var rows = Render(6, t =>
        {
            t.Append("kept row", TranscriptStyle.Prose);
            var mark = t.CommittedRowCount;
            t.Append("SUPERSEDED", TranscriptStyle.Prose);
            t.DropRowsSince(mark);
        });

        Assert.Contains(rows, row => row.Contains("kept row"));
        Assert.DoesNotContain(rows, row => row.Contains("SUPERSEDED"));
    }

    private static string[] Render(int rows, Action<TgTranscriptView> act)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var transcript = new TgTranscriptView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(transcript);
        act(transcript);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { app.RequestStop(); return false; });
        app.Run(window);
        return TgSnapshot.Capture(app);
    }

    [Fact]
    public void Overflow_PinsTheTail()
    {
        var rows = Render(24, t =>
        {
            for (var i = 0; i < 60; i++)
            {
                t.Append($" assistant line {i:D2}", TranscriptStyle.Prose);
            }
        });

        Assert.Contains("assistant line 59", rows[^1]);
        Assert.DoesNotContain("assistant line 00", string.Join('\n', rows));
    }

    [Fact]
    public void AppendChunk_GrowsTheSameLine()
    {
        var rows = Render(24, t =>
        {
            t.Append("Em ", TranscriptStyle.Prose);
            t.AppendChunk("kiểm tra ");
            t.AppendChunk("lock.");
        });

        Assert.Contains("Em kiểm tra lock.", rows[0]);
    }

    [Fact]
    public void LongLine_WrapsInsideTheViewport()
    {
        var rows = Render(24, t => t.Append(new string('x', 200), TranscriptStyle.Prose));

        Assert.Contains("x", rows[0]);
        Assert.Contains("x", rows[1]);
    }

    [Fact]
    public void AppendAfterFirstDraw_WrapsAndPinsTheTail()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var transcript = new TgTranscriptView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(transcript);
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            transcript.Append("first paint", TranscriptStyle.Prose);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            for (var i = 0; i < 60; i++)
            {
                transcript.Append($" late line {i:D2}", TranscriptStyle.Prose);
            }

            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Contains("late line 59", rows[^1]);
            Assert.DoesNotContain("late line 00", string.Join('\n', rows));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void LongLine_HardWrapsWithoutLosingText()
    {
        var text = new string('x', 200);
        var rows = Render(24, t => t.Append(text, TranscriptStyle.Prose));

        var joined = string.Join(string.Empty, rows);
        Assert.DoesNotContain("…", joined);
        Assert.Contains(text, joined);
    }

    [Fact]
    public void EmptyLine_OccupiesOneRow()
    {
        var rows = Render(24, t =>
        {
            t.Append("top", TranscriptStyle.Prose);
            t.Append(string.Empty, TranscriptStyle.Prose);
            t.Append("bottom", TranscriptStyle.Prose);
        });

        Assert.Contains("top", rows[0]);
        Assert.Equal(string.Empty, rows[1].TrimEnd());
        Assert.Contains("bottom", rows[2]);
    }

    /// <summary>I3: `/clear` drops rows, wrap cache and any collapsed block.</summary>
    [Fact]
    public void Clear_DropsRowsAndTheCollapsedBlock()
    {
        var rows = Render(24, t =>
        {
            t.Append("before clear", TranscriptStyle.Prose);
            t.AppendCollapsed("    │ · 2 more lines · ctrl+o", ["hidden diff row"], TranscriptStyle.Tool);
            t.ToggleExpanded();
            t.Clear();
            Assert.False(t.IsExpanded);
            t.Append("after clear", TranscriptStyle.Prose);
        });

        var text = string.Join('\n', rows);
        Assert.Contains("after clear", text);
        Assert.DoesNotContain("before clear", text);
        Assert.DoesNotContain("hidden diff row", text);
        Assert.DoesNotContain("ctrl+o", text);
    }

    [Fact]
    public void Retention_BoundsRowsCharactersAndCollapsedContentWithVisibleNotice()
    {
        var transcript = new TgTranscriptView();
        for (var i = 0; i < TgTranscriptView.MaxRetainedRows + 2; i++)
        {
            transcript.Append($"row-{i:D4}-{new string('x', TgTranscriptView.MaxRetainedLineCharacters)}", TranscriptStyle.Prose);
        }

        var hiddenRows = Enumerable.Range(0, 20)
            .Select(_ => new string('h', TgTranscriptView.MaxRetainedLineCharacters))
            .ToArray();

        Assert.True(transcript.IsHistoryTruncated);
        Assert.True(transcript.RetainedRowCount <= TgTranscriptView.MaxRetainedRows);
        Assert.True(transcript.RetainedCharacterCount <= TgTranscriptView.MaxRetainedCharacters);
        Assert.Contains(transcript.Lines, row => row.Text.Contains("earlier transcript rows truncated", StringComparison.OrdinalIgnoreCase));

        var collapsed = new TgTranscriptView();
        collapsed.AppendCollapsed("collapsed summary", hiddenRows, TranscriptStyle.Tool);
        collapsed.ToggleExpanded();
        Assert.True(collapsed.IsHistoryTruncated);
        Assert.True(collapsed.CollapsedRows.Count > 0);
        Assert.True(collapsed.RetainedRowCount <= TgTranscriptView.MaxRetainedRows);
        Assert.True(collapsed.RetainedCharacterCount <= TgTranscriptView.MaxRetainedCharacters);
        Assert.Contains(collapsed.Lines, row => row.Text.Contains("earlier transcript rows truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StreamingAndUnicodeLines_AreBoundedWithoutSplittingSurrogatePairs()
    {
        var transcript = new TgTranscriptView();
        transcript.Append("start", TranscriptStyle.Prose);
        transcript.AppendChunk(string.Concat(Enumerable.Repeat("😀", TgTranscriptView.MaxRetainedCharacters)));

        Assert.True(transcript.RetainedCharacterCount <= TgTranscriptView.MaxRetainedCharacters);
        Assert.DoesNotContain('\uFFFD', transcript.Lines[^1].Text);
        Assert.True(transcript.Lines[^1].Text.EnumerateRunes().Count() <= TgTranscriptView.MaxRetainedLineCharacters);
    }

    [Fact]
    public void DetachedViewport_MarksNewOutputAndClearsIndicatorAtTailOrClear()
    {
        var transcript = new TgTranscriptView();
        transcript.ScrollToTop();
        transcript.Append("new row", TranscriptStyle.Prose);
        Assert.True(transcript.HasUnseenOutput);

        transcript.AppendChunk(" streamed");
        Assert.True(transcript.HasUnseenOutput);

        transcript.ScrollToTail();
        Assert.False(transcript.HasUnseenOutput);

        transcript.ScrollToTop();
        transcript.Append("another row", TranscriptStyle.Prose);
        transcript.Clear();
        Assert.False(transcript.HasUnseenOutput);
    }

    [Fact]
    public void Viewport_ScrollsByRowsAndPagesAndExplicitlyReturnsToTail()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var transcript = new TgTranscriptView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(transcript);
        for (var i = 0; i < 40; i++)
        {
            transcript.Append($"row-{i:D2}", TranscriptStyle.Prose);
        }

        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            transcript.ScrollRows(-3);
            var scrolledPosition = transcript.ScrollPosition;
            transcript.AppendChunk(" streamed");
            Assert.Equal(scrolledPosition, transcript.ScrollPosition);
            Assert.False(transcript.IsFollowingTail);

            transcript.Page(-1);
            Assert.True(transcript.ScrollPosition < scrolledPosition);
            transcript.ScrollToTail();
            Assert.True(transcript.IsFollowingTail);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
