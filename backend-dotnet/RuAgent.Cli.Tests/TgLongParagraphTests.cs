using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgLongParagraphTests
{
    [Fact]
    public void Storage_segments_do_not_add_visual_line_breaks_to_one_paragraph()
    {
        WithWriter((view, writer) =>
        {
            var text = new string('a', 32000) + "TAIL";
            writer.CommitPresentation(new TuiMarkdown().RenderParagraphPresentation(text));
            Assert.Equal(text, view.CopyText());
            foreach (var width in new[] { 79, 120, 44 })
            {
                view.Frame = new System.Drawing.Rectangle(0, 0, width, 12);
                var expectedRows = (text.Length + view.Viewport.Width - 1) / view.Viewport.Width;
                Assert.Equal(Math.Max(0, expectedRows - view.Viewport.Height), view.ScrollPosition);
            }
        });
    }

    [Theory]
    [InlineData("alpha beta ")]
    [InlineData("alpha  beta ")]
    [InlineData("emoji \U0001f600 beta ")]
    public void Storage_segments_reflow_like_the_complete_paragraph(string phrase)
    {
        WithWriter((view, writer) =>
        {
            var text = string.Concat(Enumerable.Repeat(phrase, 3200)) + "TAIL";
            writer.CommitPresentation(new TuiMarkdown().RenderParagraphPresentation(text));
            foreach (var width in new[] { 79, 120, 44 })
            {
                view.Frame = new System.Drawing.Rectangle(0, 0, width, 12);
                IReadOnlyList<TgSpan> spans = [new TgSpan(text, new Terminal.Gui.Drawing.Attribute())];
                var expected = phrase.Contains("  ", StringComparison.Ordinal)
                    ? TgAnsiPaint.HardWrapSpans(text, spans, view.Viewport.Width)
                    : TgAnsiPaint.WordWrapSpans(text, spans, view.Viewport.Width);
                Assert.Equal(expected.Select(row => row.Row), view.WrappedLines.Select(row => row.Text));
                Assert.All(view.WrappedLines, row =>
                {
                    if (row.Spans is not null) Assert.Equal(row.Text, string.Concat(row.Spans.Select(span => span.Text)));
                });
            }
        });
    }

    [Fact]
    public void Streaming_checkpoints_reflow_after_tail_growth_and_resize()
    {
        WithWriter((view, writer) =>
        {
            var parser = new StreamingParagraphParser();
            var text = "";
            view.Frame = new System.Drawing.Rectangle(0, 0, 79, 12);
            foreach (var count in new[] { 500, 700, 13, 900, 17 })
            {
                var delta = string.Concat(Enumerable.Repeat("alpha beta ", count));
                text += delta;
                parser.Append(delta);
                writer.PreviewPresentation(parser.Preview());
                var expected = TgAnsiPaint.WordWrapSpans(text, [new TgSpan(text, new Terminal.Gui.Drawing.Attribute())], view.Viewport.Width);
                Assert.Equal(expected.Select(row => row.Row), view.WrappedLines.Select(row => row.Text));
            }
            writer.CommitPresentation(parser.Preview());
            Assert.Equal(text.TrimEnd(), view.CopyText().TrimEnd());
        });
    }

    [Theory]
    [InlineData("double  gap ")]
    [InlineData("> ")]
    [InlineData("- ")]
    public void Structured_paragraph_segments_keep_the_original_wrap_mode_and_affixes(string prefix)
    {
        WithWriter((view, writer) =>
        {
            var source = prefix + string.Concat(Enumerable.Repeat("alpha beta ", 3200)) + "TAIL";
            writer.CommitPresentation(new TuiMarkdown().RenderPresentation(source, 79));
            var text = string.Concat(view.Lines.Select(row => row.Text));
            var spans = view.Lines.SelectMany(row => row.Spans ?? [new TgSpan(row.Text, new Terminal.Gui.Drawing.Attribute())]).ToArray();
            foreach (var width in new[] { 79, 44, 120 })
            {
                view.Frame = new System.Drawing.Rectangle(0, 0, width, 12);
                var expected = TgAnsiPaint.HardWrapSpans(text, spans, view.Viewport.Width);
                Assert.Equal(expected.Select(row => row.Row), view.WrappedLines.Select(row => row.Text));
            }
        });
    }

    [Fact]
    public void Late_double_space_reflows_the_logical_paragraph_then_keeps_its_mode()
    {
        WithWriter((view, writer) =>
        {
            var parser = new StreamingParagraphParser();
            var text = "";
            view.Frame = new System.Drawing.Rectangle(0, 0, 79, 12);
            foreach (var delta in new[] { string.Concat(Enumerable.Repeat("alpha beta ", 500)), "two  spaces", " then more", " again" })
            {
                text += delta;
                parser.Append(delta);
                writer.PreviewPresentation(parser.Preview());
                var spans = new[] { new TgSpan(text, new Terminal.Gui.Drawing.Attribute()) };
                var expected = text.Contains("  ", StringComparison.Ordinal)
                    ? TgAnsiPaint.HardWrapSpans(text, spans, view.Viewport.Width)
                    : TgAnsiPaint.WordWrapSpans(text, spans, view.Viewport.Width);
                Assert.Equal(expected.Select(row => row.Row), view.WrappedLines.Select(row => row.Text));
            }
        });
    }

    [Fact]
    public void Soft_lines_form_a_long_paragraph_without_losing_its_tail_or_copy_text()
    {
        WithWriter((view, writer) =>
        {
            var renderer = AgentCommands.CreateSessionRenderer(writer);
            var lines = Enumerable.Range(0, 240).Select(i => $"{i:D3} " + new string('a', 100)).ToArray();
            renderer.Transcript.AppendAssistant(string.Join('\n', lines) + " TAIL_SENTINEL\n\n");
            renderer.Flush();
            Assert.Equal(string.Join(' ', lines) + " TAIL_SENTINEL", view.CopyText().Trim());
            Assert.All(view.Lines, row => Assert.True(row.Text.Length <= TgTranscriptView.MaxRetainedLineCharacters));
            Assert.False(view.IsHistoryTruncated);
        });
    }

    [Fact]
    public void Growing_and_shrinking_previews_replace_the_entire_paragraph_and_commit_once()
    {
        WithWriter((view, writer) =>
        {
            var markdown = new TuiMarkdown();
            foreach (var length in new[] { 9000, 24000, 33000, 12000, 26000 })
            {
                var text = new string('a', length) + " tail";
                writer.PreviewPresentation(markdown.RenderParagraphPresentation(text));
                Assert.Equal(text, view.CopyText());
            }
            var final = new string('b', 27000) + " FINAL";
            writer.CommitPresentation(markdown.RenderParagraphPresentation(final));
            writer.CommitPresentation(markdown.RenderParagraphPresentation("next paragraph"));
            Assert.Equal(final + Environment.NewLine + "next paragraph", view.CopyText());
            view.Frame = new System.Drawing.Rectangle(0, 0, 44, 12);
            Assert.True(view.CanScroll);
            view.ScrollToTop();
            view.Frame = new System.Drawing.Rectangle(0, 0, 120, 30);
            Assert.True(view.CanScroll);
            Assert.Equal(final + Environment.NewLine + "next paragraph", view.CopyText());
        });
    }

    [Fact]
    public void Unicode_tabs_and_inline_styles_survive_segment_boundaries()
    {
        WithWriter((view, writer) =>
        {
            var text = new string('x', 15998) + "😀 tiếng Việt **" + new string('b', 17000) + "**\tTAIL";
            var runs = new TuiMarkdown().RenderParagraphPresentation(text);
            writer.PreviewPresentation(runs);
            writer.CommitPresentation(runs);
            var expected = TuiText.ExpandTabs(string.Concat(runs.Select(row => row.Text)), 0, out _);
            Assert.Equal(expected, view.CopyText());
            Assert.DoesNotContain('�', view.CopyText());
            Assert.All(view.Lines, row =>
            {
                Assert.False(row.Text.Length > 0 && char.IsHighSurrogate(row.Text[^1]));
                Assert.False(row.Text.Length > 0 && char.IsLowSurrogate(row.Text[0]));
                if (row.Spans is not null) Assert.Equal(row.Text, string.Concat(row.Spans.Select(span => span.Text)));
            });
            Assert.Equal(17000, view.Lines.SelectMany(row => row.Spans ?? []).Where(span => span.Attr.Style == Terminal.Gui.Drawing.TextStyle.Bold).Sum(span => span.Text.Length));
            Assert.Equal(17000, view.WrappedLines.SelectMany(row => row.Spans ?? []).Where(span => span.Attr.Style == Terminal.Gui.Drawing.TextStyle.Bold).Sum(span => span.Text.Length));
        });
    }

    [Fact]
    public void Retry_and_suppression_remove_all_segments_but_keep_tool_receipts()
    {
        WithWriter((view, writer) =>
        {
            writer.BeginAssistantStep("step_1");
            writer.CommitPresentation(new TuiMarkdown().RenderParagraphPresentation(new string('a', 25000)));
            writer.AppendActivityPresentation("tool_1", TranscriptLine.Plain("tool receipt", TranscriptRole.Tool));
            writer.RewindAssistantStep("step_1");
            Assert.DoesNotContain(view.Lines, row => row.Style == TranscriptStyle.Prose);
            Assert.Contains(view.Lines, row => row.Text == "tool receipt");
            writer.BeginAssistantStep("step_2");
            writer.PreviewPresentation(new TuiMarkdown().RenderParagraphPresentation(new string('b', 26000)));
            writer.RewindAssistantStep("step_2");
            writer.CommitPresentation(new TuiMarkdown().RenderParagraphPresentation("replacement"));
            Assert.Equal("replacement", view.CopyText());
            Assert.Contains(view.Lines, row => row.Text == "tool receipt");
        });
    }

    [Fact]
    public async Task Durable_recovery_preserves_a_long_paragraph_and_prior_turn()
    {
        Task<bool>? repair = null;
        var text = new string('r', 35000) + " RECOVERED_TAIL";
        WithWriter((view, writer) =>
        {
            writer.CommitPresentation([TranscriptLine.Plain("earlier turn")]);
            writer.AppendPresentation(TranscriptLine.Plain("user", TranscriptRole.User));
            var renderer = AgentCommands.CreateSessionRenderer(writer);
            renderer.BeginTurn();
            renderer.Transcript.AppendAssistant(new string('x', 23000));
            repair = renderer.Transcript.RecoverAssistantTurnAsync(text);
            Assert.Equal(text, view.CopyText());
            Assert.Contains(view.Lines, row => row.Text == "earlier turn");
            Assert.DoesNotContain(view.Lines, row => row.Text.Contains("xxxxx", StringComparison.Ordinal));
        });
        Assert.NotNull(repair);
        Assert.True(await repair);
    }

    [Fact]
    public void Fragmented_stream_commits_one_complete_styled_paragraph()
    {
        WithWriter((view, writer) =>
        {
            var text = "**" + string.Concat(Enumerable.Repeat("Việt😀 ", 4000)) + "** END";
            var renderer = AgentCommands.CreateSessionRenderer(writer);
            renderer.BeginTurn();
            // Many deltas cross storage boundaries; keep transport fragments rune-safe.
            var offset = 0;
            while (offset < text.Length)
            {
                var count = Math.Min(1307, text.Length - offset);
                if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1])) count--;
                renderer.Transcript.AppendAssistant(text.Substring(offset, count));
                offset += count;
            }
            renderer.Flush();
            Assert.Equal(string.Concat(new TuiMarkdown().RenderParagraphPresentation(text).Select(row => row.Text)), view.CopyText().Trim());
            Assert.EndsWith(" END", view.CopyText());
        });
    }

    [Fact]
    public void Streaming_checkpoints_preserve_tab_columns_and_reuse_native_prefix_rows()
    {
        WithWriter((view, writer) =>
        {
            var parser = new StreamingParagraphParser();
            var text = string.Concat(Enumerable.Repeat("plain words **bold** and `code` ", 200)) + "tail";
            parser.Append(text);
            writer.PreviewPresentation(parser.Preview());
            var first = view.Lines[0];
            parser.Append("\tnew tail");
            writer.PreviewPresentation(parser.Preview());
            Assert.Same(first, view.Lines[0]);
            var expected = string.Concat(MarkdigInline.Parse(text + "\tnew tail").Select(run => run.Text));
            Assert.Equal(TuiText.ExpandTabs(expected, 0, out _), view.CopyText());
            writer.CommitPresentation(parser.Preview());
            Assert.Equal(TuiText.ExpandTabs(expected, 0, out _), view.CopyText());
        });
    }

    [Fact]
    public void Cached_streaming_prefix_does_not_reinsert_evicted_history()
    {
        WithWriter((view, writer) =>
        {
            var parser = new StreamingParagraphParser();
            var text = "OLD_HEAD " + string.Concat(Enumerable.Range(0, 2600).Select(i => $"word{i:D4} " + new string('x', 92) + " "));
            for (var offset = 0; offset < text.Length; offset += 1024)
            {
                parser.Append(text.Substring(offset, Math.Min(1024, text.Length - offset)));
                writer.PreviewPresentation(parser.Preview());
                Assert.InRange(view.RetainedCharacterCount, 1, TgTranscriptView.MaxRetainedCharacters);
            }
            parser.Append("TAIL_SENTINEL");
            writer.CommitPresentation(parser.Preview());
            Assert.True(view.IsHistoryTruncated);
            Assert.DoesNotContain("OLD_HEAD", view.CopyText());
            Assert.EndsWith("TAIL_SENTINEL", view.CopyText());
            Assert.Equal(1, view.Lines.Count(row => row.Text.Contains("earlier transcript rows truncated")));
        });
    }

    [Fact]
    public void Tool_group_summary_precedes_the_streaming_paragraph_without_replacing_its_prefix()
    {
        WithWriter((view, writer) =>
        {
            var renderer = AgentCommands.CreateSessionRenderer(writer);
            renderer.BeginTurn();
            foreach (var id in new[] { "activity_one", "activity_two" })
            {
                renderer.Transcript.ApplyActivity(new AgentActivityModel(id, AgentActivityKind.Search,
                    AgentActivityState.Completed, "README.md", null, null, null, 1, null, null, 12,
                    "succeeded", "Search", null, null));
                renderer.Transcript.ApplyDurable(new(1, "evt_" + id, "run_1", "session_1", 1,
                    "tool.execution_completed", "2026-10-25T00:00:00Z", "succeeded", "ok"));
            }
            var reply = string.Concat(Enumerable.Repeat("answer words **bold** ", 300)) + "TAIL";
            renderer.Transcript.AppendAssistant(reply);
            renderer.Flush();
            Assert.Equal(string.Concat(MarkdigInline.Parse(reply).Select(run => run.Text)), view.CopyText().Trim());
            Assert.Equal(string.Concat(MarkdigInline.Parse(reply).Select(run => run.Text)),
                string.Concat(view.Lines.Where(row => row.Style == TranscriptStyle.Prose).Select(row => row.Text)).Trim());
            var summary = Assert.Single(view.Lines, row => row.Text.Contains("2 tools ·", StringComparison.Ordinal));
            var firstProse = view.Lines.ToList().FindIndex(row => row.Text.Contains("answer words", StringComparison.Ordinal));
            Assert.True(view.Lines.ToList().IndexOf(summary) < firstProse);
        });
    }

    [Fact]
    public void Oversized_paragraph_keeps_the_newest_tail_within_total_retention_budget()
    {
        WithWriter((view, writer) =>
        {
            var markdown = new TuiMarkdown();
            writer.CommitPresentation([TranscriptLine.Plain("older answer")]);
            foreach (var length in new[] { 210000, 250000, 260000 })
            {
                writer.PreviewPresentation(markdown.RenderParagraphPresentation(new string('z', length) + " TAIL"));
                Assert.EndsWith(" TAIL", view.CopyText());
                Assert.DoesNotContain("older answer", view.CopyText());
                Assert.InRange(view.RetainedCharacterCount, 1, TgTranscriptView.MaxRetainedCharacters);
                Assert.InRange(view.RetainedRowCount, 1, TgTranscriptView.MaxRetainedRows);
                Assert.True(view.IsHistoryTruncated);
                view.Frame = new System.Drawing.Rectangle(0, 0, length == 250000 ? 44 : 79, 12);
                Assert.Contains(view.WrappedLines, row => row.Style == TranscriptStyle.Meta && row.Text.Contains("earlier transcript"));
                Assert.DoesNotContain(view.WrappedLines, row => row.Style == TranscriptStyle.Prose && row.Text.Contains("truncated"));
            }
            writer.PreviewPresentation(markdown.RenderParagraphPresentation("short replacement"));
            writer.CommitPresentation(markdown.RenderParagraphPresentation("short replacement"));
            Assert.Equal("short replacement", view.CopyText());
        });
    }

    private static void WithWriter(Action<TgTranscriptView, TgTranscriptWriter> action)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            try { action(window.Transcript, writer); }
            finally { app.RequestStop(); }
            return false;
        });
        app.Run(window);
    }
}
