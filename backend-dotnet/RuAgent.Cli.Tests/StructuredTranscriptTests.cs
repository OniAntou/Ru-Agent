using System.Text;
using RuAgent.Cli.Tui;
using RuAgent.Cli.TerminalGui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class StructuredTranscriptTests
{
    [Fact]
    public void Native_diff_attributes_preserve_file_headers_and_line_colors()
    {
        using var color = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        string[] lines = ["--- a/file.cs", "+++ b/file.cs", "@@ -1 +1 @@", "-old", "+new"];
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        var surface = new TuiSurface(writer);
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { FullScreenCards = true };
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            transcript.ApplyDiffText(string.Join('\n', lines) + "\n context", 80);
            Assert.Equal(lines.Length, window.Transcript.Lines.Count);
            for (var index = 0; index < lines.Length; index++)
            {
                var expected = TgAnsiPaint.Parse("    " + TuiPalette.Paint("│ ", TuiToken.Line) + TuiTranscript.FormatDiffLine(lines[index]));
                var actual = window.Transcript.Lines[index];
                Assert.Equal(expected.Clean, actual.Text);
                Assert.Equal(expected.Spans!.SelectMany(span => span.Text.Select(c => (c, span.Attr))),
                    actual.Spans!.SelectMany(span => span.Text.Select(c => (c, span.Attr))));
            }
            app.RequestStop(); return false;
        });
        app.Run(window);
    }

    [Theory]
    [InlineData("# `a``b` tail")]
    [InlineData("# ~~a~~~~b~~ tail")]
    public void Adjacent_inline_styles_close_the_enclosing_heading_after_the_first_fragment(string source)
    {
        // Delimiter pairing is CommonMark's job now: a length-2 backtick run cannot close a
        // length-1 opener, so the pathological cases are one styled span plus a tail. What
        // this test pins is the heading's paint boundary: the styled fragment keeps the
        // heading style, the trailing text must not inherit it.
        var row = Assert.Single(new TuiMarkdown().RenderPresentation(source, 120));
        var styled = row.Runs[0];
        var tail = Assert.Single(row.Runs, run => run.Text == " tail");
        Assert.True(styled.Bold);
        Assert.DoesNotContain(" tail", styled.Text, StringComparison.Ordinal);
        Assert.False(tail.Bold);
        Assert.False(tail.Dim);
    }

    [Theory]
    [InlineData("# Heading **bold** tail")]
    [InlineData("# Heading `code` and **bold** tail")]
    [InlineData("## Heading ~~strike~~ and `code` tail")]
    public void Native_heading_attributes_match_existing_inline_boundaries(string source)
    {
        using var color = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        var old = Assert.Single(new TuiMarkdown().RenderLogicalLine(source, 120));
        var expected = TgAnsiPaint.Parse(old);
        var row = Assert.Single(new TuiMarkdown().RenderPresentation(source, 120));
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            writer.AppendPresentation(row);
            var actual = Assert.Single(window.Transcript.Lines);
            Assert.Equal(expected.Clean, actual.Text);
            var expectedCells = expected.Spans!.SelectMany(span => span.Text.Select(c => (c, span.Attr))).ToArray();
            var actualCells = actual.Spans!.SelectMany(span => span.Text.Select(c => (c, span.Attr))).ToArray();
            Assert.Equal(expectedCells, actualCells);
            app.RequestStop(); return false;
        });
        app.Run(window);
    }

    [Theory]
    [InlineData("hello **important** and `README.md`")]
    [InlineData("# Heading")]
    [InlineData("## Header **important**")]
    [InlineData("- bullet with `code`")]
    [InlineData("  - nested **text**")]
    [InlineData("    - deeper list")]
    [InlineData("12. numbered item")]
    [InlineData("> quoted **text**")]
    [InlineData("[label](https://example.invalid)")]
    [InlineData("escaped \\*literal\\* and ~~strike~~")]
    [InlineData("word__inside__word")]
    [InlineData("| name | value |")]
    [InlineData("**name** | `value`")]
    [InlineData("```label|variant")]
    [InlineData("✓ this is assistant prose, not a tool")]
    [InlineData("Tiếng Việt 😀 中文 e\u0301")]
    [InlineData("")]
    public void Structured_markdown_keeps_the_existing_logical_text(string source)
    {
        using var color = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        var legacy = new TuiMarkdown { HighlightCode = true }.RenderLogicalLine(source, 80);
        var native = new TuiMarkdown { HighlightCode = true }.RenderPresentation(source, 80);
        Assert.Equal(legacy.Select(TuiText.Strip), native.Select(row => row.Text));
        Assert.All(native, row => Assert.DoesNotContain('\u001b', row.Text));
        Assert.All(native, row => Assert.Equal(TranscriptRole.Prose, row.Role));
    }

    [Fact]
    public void Code_preview_never_advances_fence_state_or_line_numbers()
    {
        var markdown = new TuiMarkdown { HighlightCode = true };
        Assert.Empty(markdown.PreviewPresentation("```csharp", 80));
        Assert.False(markdown.InFence);
        markdown.RenderPresentation("```csharp", 80);
        var one = markdown.PreviewPresentation("const string x = \"hello\";", 80);
        Assert.Equal(one.Select(row => row.Text), markdown.PreviewPresentation("const string x = \"hello\";", 80).Select(row => row.Text));
        Assert.StartsWith("  1 │ ", Assert.Single(markdown.RenderPresentation("const string x = \"hello\";", 80)).Text);
        Assert.StartsWith("  2 │ ", Assert.Single(markdown.RenderPresentation("return 42;", 80)).Text);
        Assert.Empty(markdown.RenderPresentation("```", 80));
        Assert.False(markdown.InFence);
        Assert.Contains(one.Single().Runs, run => run.Text == "const" && run.Token == TuiToken.Brand2);
        Assert.Contains(one.Single().Runs, run => run.Text == "\"hello\"" && run.Token == TuiToken.Ok);
    }

    [Theory]
    [InlineData(ToolLineStatus.Ok)]
    [InlineData(ToolLineStatus.Failed)]
    [InlineData(ToolLineStatus.Unknown)]
    [InlineData(ToolLineStatus.Running)]
    public void Native_tool_layout_preserves_text_at_narrow_and_wide_widths(ToolLineStatus status)
    {
        using var color = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        foreach (var width in new[] { 8, 14, 25, 80, 120 })
        {
            var row = TuiToolRow.ComposePresentation(status, "read_file", "folder/README.md", "(18 matches) · 12ms", width);
            Assert.Equal(TuiText.Strip(TuiToolRow.Compose(status, "read_file", "folder/README.md", "(18 matches) · 12ms", width)), row.Text);
            Assert.Equal(TranscriptRole.Tool, row.Role);
            Assert.DoesNotContain('\u001b', row.Text);
        }
    }

    [Theory]
    [InlineData(TuiTurnOutcome.Completed)]
    [InlineData(TuiTurnOutcome.Cancelled)]
    [InlineData(TuiTurnOutcome.Failed)]
    public async Task Event_rendering_uses_typed_presentation_without_writing_ansi(TuiTurnOutcome outcome)
    {
        using var color = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        var writer = new PresentationProbe();
        var surface = new TuiSurface(writer);
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { FullScreenCards = true };
        transcript.BeginTurn();
        transcript.AppendAssistant("# Heading\nHi **there** and `README.md`\n```cs\nreturn 42;\n```\n");
        transcript.Note("provider_timeout", TuiToken.Warn);
        transcript.ApplyActivity(new AgentActivityModel("act_1", AgentActivityKind.Search,
            AgentActivityState.Started, "README.md", null, null, null, null, null, null, null, "started", "Search", null, null));
        transcript.ApplyDurable(new(1, "evt_1", "run_1", "sess_1", 1, "tool.execution_completed", "2026-10-20T00:00:00Z", "succeeded", "ok"));
        transcript.ApplyActivity(new AgentActivityModel("act_1", AgentActivityKind.Search,
            AgentActivityState.Completed, "README.md", null, null, null, 3, null, null, 12, "succeeded", "Search", 1, 2));
        transcript.ApplyDiffText("@@ -1 +1 @@\n-old\n+new", 80);
        Assert.True(await transcript.RecoverAssistantTurnAsync("Recovered **answer**\n"));
        transcript.EndTurn(outcome, "provider_timeout");
        Assert.True(writer.Replaced);
        Assert.Contains(writer.Rows, row => row.Role == TranscriptRole.Tool);
        Assert.Contains(writer.Rows, row => row.Runs.Any(run => run.Token == TuiToken.Warn));
        Assert.Contains(writer.Rows, row => row.Text.Contains("3 matches"));
        Assert.All(writer.Rows, row => Assert.DoesNotContain('\u001b', row.Text));
    }

    [Fact]
    public void Assistant_tool_glyphs_keep_prose_identity_and_native_paint()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        var surface = new TuiSurface(writer);
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { FullScreenCards = true };
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            transcript.AppendAssistant("✓ **answer** in `README.md`\n");
            transcript.Flush();
            var row = Assert.Single(window.Transcript.Lines, row => row.Text.Contains("answer"));
            Assert.Equal(TranscriptStyle.Prose, row.Style);
            Assert.Contains(row.Spans!, span => span.Text == "answer" && span.Attr.Foreground == TgPalette.ToColor(TuiToken.Bright));
            Assert.Contains(row.Spans!, span => span.Text == "README.md" && span.Attr.Foreground == TgPalette.ToColor(TuiToken.Ok));
            app.RequestStop(); return false;
        });
        app.Run(window);
    }

    private sealed class PresentationProbe : TextWriter, ITranscriptPresentationWriter, ITranscriptLineWriter, ITranscriptTurnRewriter, ITranscriptActivityWriter
    {
        internal List<TranscriptLine> Rows { get; } = [];
        internal bool Replaced;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Assert.True(char.IsWhiteSpace(value), "unstructured text reached the interactive writer");
        public override void Write(string? value) => Assert.True(string.IsNullOrWhiteSpace(value), "unstructured text reached the interactive writer");
        public override void WriteLine() { }
        public void PreviewPresentation(IReadOnlyList<TranscriptLine> rows) => Rows.AddRange(rows);
        public void CommitPresentation(IReadOnlyList<TranscriptLine> rows) => Rows.AddRange(rows);
        public void AppendPresentation(TranscriptLine row) => Rows.Add(row);
        public void AppendPresentationChunk(TranscriptLine row) => Rows.Add(row);
        public void AppendActivityPresentation(string id, TranscriptLine row, IReadOnlyList<string>? details = null) => Rows.Add(row);
        public void UpdateActivityPresentation(string id, TranscriptLine row) => Rows.Add(row);
        public Task<bool> ReplaceAssistantPresentationAsync(IReadOnlyList<TranscriptLine> rows) { Rows.AddRange(rows); Replaced = true; return Task.FromResult(true); }
        public void PreviewLine(string text) => Assert.Empty(text);
        public void CommitLine(IReadOnlyList<string> rows) => throw new InvalidOperationException("legacy Markdown commit");
        public void RetainDetails(IReadOnlyList<string> rows) { Assert.All(rows, row => Assert.DoesNotContain('\u001b', row)); }
        public void BeginAssistantTurn() { }
        public Task<bool> ReplaceAssistantTurnAsync(IReadOnlyList<string> rows) => throw new InvalidOperationException("legacy recovery");
        public void AppendToolRow(string id, string text) => throw new InvalidOperationException("legacy tool append");
        public void UpdateToolRow(string id, string text) => throw new InvalidOperationException("legacy tool update");
    }
}
