using System.Net;
using System.Text;
using RuAgent.Cli;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// T5a integration: the AltBuffer lifecycle and the three-region frame. The chat REPL cannot
/// own a live console inside the test host (stdout is redirected, so the gate falls back), so
/// these tests drive the exact pieces the REPL wires — the scope, the pinned header and the
/// sticky region with a transcript top — and assert what the screen shows through
/// <see cref="TuiScreenSimulator"/>. Fallback equivalence is asserted as: a disabled scope
/// writes no alt-buffer escape and leaves the sequential frame byte-identical.
/// </summary>
public sealed class TuiAltBufferIntegrationTests
{
    private const int Cols = 100;

    private static (StringWriter Writer, TuiSurface Surface, TuiScreenSimulator Sim) NewFrame(int rows)
    {
        var writer = new StringWriter();
        var surface = new TuiSurface(writer) { SynchronizedOutput = false };
        return (writer, surface, new TuiScreenSimulator(rows: rows, cols: Cols));
    }

    [Fact]
    public void AltBufferSession_EmitsEnterAndExitAroundOneFrame()
    {
        var (writer, surface, sim) = NewFrame(rows: 30);

        using (var scope = new TuiAltBufferScope(surface, enabled: true))
        {
            Assert.True(scope.Active);
            surface.WriteLineVolatile("✦ Ru · coding agent");
        }

        var text = writer.ToString();
        Assert.StartsWith(TuiAltBuffer.Enter, text, StringComparison.Ordinal);
        Assert.EndsWith(TuiAltBuffer.Exit, text, StringComparison.Ordinal);
        sim.Write(text);
        Assert.Contains("Ru", sim.ScreenText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AltBufferSession_EmitsExitWhenTheSessionThrows()
    {
        var (writer, surface, _) = NewFrame(rows: 30);

        Action failingSession = () =>
        {
            using var scope = new TuiAltBufferScope(surface, enabled: true);
            throw new InvalidOperationException("session failed");
        };

        Assert.Throws<InvalidOperationException>(failingSession);

        var text = writer.ToString();
        Assert.StartsWith(TuiAltBuffer.Enter, text, StringComparison.Ordinal);
        Assert.EndsWith(TuiAltBuffer.Exit, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AltBufferSession_ExitsOnlyOnce()
    {
        var (writer, surface, _) = NewFrame(rows: 30);

        var scope = new TuiAltBufferScope(surface, enabled: true);
        scope.Dispose();
        scope.Dispose();

        Assert.Equal(TuiAltBuffer.Enter + TuiAltBuffer.Exit, writer.ToString());
    }

    [Fact]
    public void DisabledScope_LeavesTheSequentialFrameByteIdentical()
    {
        var baselineWriter = new StringWriter();
        var baseline = new TuiSurface(baselineWriter) { SynchronizedOutput = false };
        baseline.WriteLineVolatile("row one");
        baseline.WriteLine("row two");
        baseline.RepaintSingleLine("live row", Cols);

        var writer = new StringWriter();
        var surface = new TuiSurface(writer) { SynchronizedOutput = false };
        using (var scope = new TuiAltBufferScope(surface, enabled: false))
        {
            Assert.False(scope.Active);
            surface.WriteLineVolatile("row one");
            surface.WriteLine("row two");
            surface.RepaintSingleLine("live row", Cols);
        }

        Assert.Equal(baselineWriter.ToString(), writer.ToString());
        Assert.DoesNotContain("?1049", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedHeader_FillsTheHeaderRowAndParksBelow()
    {
        var (writer, surface, sim) = NewFrame(rows: 30);

        TuiHeader.RenderPinned(
            surface,
            width: Cols,
            topRow: 1,
            title: "Fix broken tests",
            tokens: 39413,
            height: 1);

        surface.Write("tail lands below the header");

        sim.Write(writer.ToString());
        Assert.Contains("# Fix broken tests", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Contains("39,413", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Contains("tail lands below the header", sim.VisibleRow(1), StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptScrolling_KeepsHeaderAndFooterOutsideTheRegion()
    {
        var (writer, surface, sim) = NewFrame(rows: 30);
        var region = new TuiStickyRegion(surface);
        var layout = TuiScreenLayout.Compute(Cols, 30);
        const int footerHeight = 4;

        TuiHeader.RenderPinned(
            surface, Cols, layout.HeaderTop + 1,
            "Scrolling session", 59, layout.HeaderHeight);
        Assert.True(region.Enter(30, footerHeight, Cols, topRow: layout.TranscriptTop + 1));

        for (var i = 0; i < 60; i++)
        {
            surface.WriteLine($"line {i:D2}");
        }

        region.Paint(["status", "top", "mid", "bottom"], Cols);

        sim.Write(writer.ToString());
        var footerFirst = 30 - footerHeight;
        Assert.Contains("Scrolling session", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.DoesNotContain("line", sim.VisibleRow(0), StringComparison.Ordinal);
        // The tracked append row is the bottom transcript row; the line's trailing newline
        // scrolls the text up one, so the last finished line sits directly above it.
        Assert.Contains("line 59", sim.VisibleRow(footerFirst - 2), StringComparison.Ordinal);
        Assert.Contains("status", sim.VisibleRow(footerFirst), StringComparison.Ordinal);
        Assert.Contains("bottom", sim.VisibleRow(29), StringComparison.Ordinal);
    }

    [Fact]
    public void Resize_RebuildsTheAssistantTailInsideTheTranscriptRegion()
    {
        var (writer, surface, sim) = NewFrame(rows: 40);
        var region = new TuiStickyRegion(surface);
        const int footerHeight = 4;

        // First frame at 30 rows, then the operator grows the window to 40.
        BuildAltFrame(surface, region, rows: 30, footerHeight, "assistant tail marker");
        BuildAltFrame(surface, region, rows: 40, footerHeight, "assistant tail marker");

        sim.Write(writer.ToString());
        var layout = TuiScreenLayout.Compute(Cols, 40);
        var footerFirst = 40 - footerHeight;

        Assert.Contains("Resize rebuild", sim.VisibleRow(0), StringComparison.Ordinal);
        Assert.Contains("assistant tail marker", sim.VisibleRow(footerFirst - 2), StringComparison.Ordinal);
        Assert.Contains("status", sim.VisibleRow(footerFirst), StringComparison.Ordinal);
        for (var row = 0; row < layout.TranscriptTop; row++)
        {
            Assert.DoesNotContain("assistant", sim.VisibleRow(row), StringComparison.Ordinal);
        }

        for (var row = footerFirst; row < 40; row++)
        {
            Assert.DoesNotContain("assistant", sim.VisibleRow(row), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WriteAltSafeAsync_SequentialFallback_IsByteIdentical()
    {
        var frameWriter = new StringWriter();
        var region = new TuiStickyRegion(new TuiSurface(frameWriter));
        Assert.True(region.Enter(30, 3, Cols));
        frameWriter.GetStringBuilder().Clear();

        var expected = new StringWriter();
        await expected.WriteLineAsync("run_error: boom");
        var actual = new StringWriter();

        await AgentCommands.WriteAltSafeAsync(region, altBufferActive: false, actual, "run_error: boom");

        Assert.Equal(expected.ToString(), actual.ToString());
    }

    [Fact]
    public async Task WriteAltSafeAsync_AltFrame_RestoresTheTranscriptCursorFirst()
    {
        var frameWriter = new StringWriter();
        var region = new TuiStickyRegion(new TuiSurface(frameWriter));
        Assert.True(region.Enter(30, 3, Cols));
        frameWriter.GetStringBuilder().Clear();

        var expected = new StringWriter();
        expected.Write("\u001b[27;1H");
        await expected.WriteLineAsync("run_error: boom");
        var actual = new StringWriter();

        await AgentCommands.WriteAltSafeAsync(region, altBufferActive: true, actual, "run_error: boom");

        // Footer starts at row 28, so the transcript bottom row is 27.
        Assert.Equal(expected.ToString(), actual.ToString());
    }

    [Fact]
    public void FullRebuildClearSequence_Drops3JOnlyOnTheAltScreen()
    {
        Assert.Equal("\u001b[r\u001b[2J\u001b[H", AgentCommands.FullRebuildClearSequence(altBufferActive: true));
        Assert.Equal("\u001b[r\u001b[3J\u001b[2J\u001b[H", AgentCommands.FullRebuildClearSequence(altBufferActive: false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ChatAsync_FallbackPaths_EmitNoAltBufferEscapes(bool classic, bool json)
    {
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var commands = CreateCommands(outWriter, errWriter, new StringReader("/exit\n"));

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\workspace", "sess_123", "standard", Json: json, Classic: classic),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("?1049", outWriter.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("?1049", errWriter.ToString(), StringComparison.Ordinal);
    }

    private static void BuildAltFrame(
        TuiSurface surface,
        TuiStickyRegion region,
        int rows,
        int footerHeight,
        string assistantTail)
    {
        region.Exit();
        surface.Repaint(writer => writer.Write("\u001b[r\u001b[3J\u001b[2J\u001b[H"));
        var layout = TuiScreenLayout.Compute(Cols, rows);
        TuiHeader.RenderPinned(
            surface, Cols, layout.HeaderTop + 1,
            "Resize rebuild", 88, layout.HeaderHeight);
        Assert.True(region.Enter(rows, footerHeight, Cols, topRow: layout.TranscriptTop + 1));
        surface.WriteLine(assistantTail);
        region.Paint(["status", "top", "mid", "bottom"], Cols);
    }

    private static PublicHostSession Session()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var descriptor = new RuAgent.Protocol.Harness.RuntimeDescriptor(
            1,
            "http://127.0.0.1:8000/",
            "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            proc.Id,
            proc.StartTime.ToUniversalTime().ToString("O"),
            new string('a', 32),
            DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(descriptor);
    }

    private static AgentCommands CreateCommands(
        StringWriter outWriter,
        StringWriter errWriter,
        TextReader inReader) =>
        new(
            Session(),
            new HttpClient(new FakeHandler(request =>
                request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.Contains("/settings", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                    })),
            outWriter,
            errWriter,
            inReader);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = func;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_func(request));
    }
}
