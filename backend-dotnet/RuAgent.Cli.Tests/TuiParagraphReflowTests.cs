using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// Paragraph reflow: consecutive prose lines become one screen row, and every block form
/// closes the paragraph first. The screen is a recording double, so the assertion is about
/// the rows the renderer committed rather than about escape sequences.
/// </summary>
public sealed class TuiParagraphReflowTests
{
    private sealed class RecordingWriter : StringWriter, ITranscriptPresentationWriter, ITranscriptLineWriter
    {
        public void PreviewLine(string text) => Previewed.Add(text);

        public void CommitLine(IReadOnlyList<string> rows) => Committed.AddRange(rows);

        public void RetainDetails(IReadOnlyList<string> rows) { }

        public List<string> Committed { get; } = [];
        public List<string> Previewed { get; } = [];

        public void PreviewPresentation(IReadOnlyList<TranscriptLine> rows) =>
            Previewed.AddRange(rows.Select(row => row.Text));

        public void CommitPresentation(IReadOnlyList<TranscriptLine> rows) =>
            Committed.AddRange(rows.Select(row => row.Text));

        public void AppendPresentation(TranscriptLine row) => Committed.Add(row.Text);

        public void AppendPresentationChunk(TranscriptLine row)
        {
            if (Committed.Count == 0) Committed.Add(row.Text);
            else Committed[^1] += row.Text;
        }

        public void AppendActivityPresentation(string activityId, TranscriptLine row, IReadOnlyList<string>? details = null) =>
            Committed.Add(row.Text);

        public void UpdateActivityPresentation(string activityId, TranscriptLine row) { }

        public Task<bool> ReplaceAssistantPresentationAsync(IReadOnlyList<TranscriptLine> rows) =>
            Task.FromResult(false);
    }

    private static (TuiTranscript Transcript, RecordingWriter Writer) Create()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var writer = new RecordingWriter();
        var surface = new TuiSurface(writer);
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { Width = 80 };
        transcript.BeginTurn();
        return (transcript, writer);
    }

    [Fact]
    public void Consecutive_prose_lines_become_one_reflowed_row()
    {
        var (transcript, writer) = Create();

        transcript.AppendAssistant("first line\nsecond line\n\nnext paragraph\n");
        transcript.Flush();

        Assert.Equal(["first line second line", string.Empty, "next paragraph"], writer.Committed);
    }

    [Fact]
    public void A_block_form_closes_the_paragraph_before_it_renders()
    {
        var (transcript, writer) = Create();

        transcript.AppendAssistant("intro line\n## Heading\ntail line\n");
        transcript.Flush();

        Assert.Equal(["intro line", "Heading", "tail line"], writer.Committed);
    }

    [Fact]
    public void A_list_closes_the_paragraph_and_keeps_its_own_rows()
    {
        var (transcript, writer) = Create();

        transcript.AppendAssistant("items:\n- one\n- two\n");
        transcript.Flush();

        Assert.Equal(3, writer.Committed.Count);
        Assert.Equal("items:", writer.Committed[0]);
        Assert.Contains("one", writer.Committed[1], StringComparison.Ordinal);
        Assert.Contains("two", writer.Committed[2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_paragraph_previews_while_it_grows()
    {
        var (transcript, writer) = Create();

        transcript.AppendAssistant("half a para");
        // Previews are throttled to ~30 fps; the second fragment lands after one interval.
        Thread.Sleep(40);
        transcript.AppendAssistant("graph");

        Assert.Contains(writer.Previewed, row => row.Contains("half a paragraph", StringComparison.Ordinal));
    }
}
