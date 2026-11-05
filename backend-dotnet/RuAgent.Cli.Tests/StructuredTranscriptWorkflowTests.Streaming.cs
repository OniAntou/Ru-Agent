using System.Diagnostics;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Xunit;
using Xunit.Abstractions;

namespace RuAgent.Cli.Tests;

public sealed partial class StructuredTranscriptWorkflowTests
{
    private readonly ITestOutputHelper _streamOutput;

    public StructuredTranscriptWorkflowTests(ITestOutputHelper output) => _streamOutput = output;

    [Fact]
    public Task A_multiline_stream_burst_bounds_UI_dispatches_and_keeps_every_code_row() =>
        RunWorker(async ui =>
        {
            const int lines = 1024;
            var source = "```cs\n" + string.Join('\n', Enumerable.Range(0, lines)
                .Select(index => $"Console.WriteLine({index});")) + "\n```\n";
            var samples = new List<(int Calls, long Bytes, double Milliseconds)>();
            for (var sample = 0; sample < 6; sample++)
            {
                await ui.OnUi(() => ui.View.Clear());
                ui.Writer.AppendPresentation(TranscriptLine.Plain("Earlier user prompt", TranscriptRole.User));
                await ui.OnUi(() => { }); // Measure chat streaming after welcome has hidden.
                var calls = 0;
                var projector = new StreamingMarkdownProjector(new TuiMarkdown { HighlightCode = true },
                    ui.Writer.PreviewPresentation, rows => { calls++; ui.Writer.CommitPresentation(rows); });
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                projector.Append(source, 100);
                projector.Flush(100);
                var producerBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                await ui.OnUi(() =>
                {
                    Assert.Equal(lines, ui.View.Lines.Count(row => row.Text.Contains("Console.WriteLine(", StringComparison.Ordinal)));
                    Assert.Contains("Console.WriteLine(0);", ui.View.Lines[2].Text);
                    Assert.Contains("Console.WriteLine(1023);", ui.View.Lines[^1].Text);
                    Assert.Equal(TranscriptStyle.User, ui.View.Lines[0].Style);
                });
                watch.Stop();
                if (sample > 0) samples.Add((calls, producerBytes, watch.Elapsed.TotalMilliseconds));
            }
            foreach (var sample in samples)
                _streamOutput.WriteLine("STREAM_BENCH|calls={0}|producer_bytes={1}|ui_apply_ms={2:F3}",
                    sample.Calls, sample.Bytes, sample.Milliseconds);
            // A burst must not enqueue a separate UI callback for each completed line.
            Assert.All(samples, sample => Assert.InRange(sample.Calls, 1, 32));
        });
}
