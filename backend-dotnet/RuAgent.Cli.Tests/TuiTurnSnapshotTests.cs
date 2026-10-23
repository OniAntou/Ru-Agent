using System.Text;
using System.Text.Json;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// One scripted turn replayed through the real frame entry point. The point is the grammar:
/// exactly one row per settled tool, prose guttered once, and a closing footer that carries the
/// summary the CLI used to ignore.
/// </summary>
public sealed class TuiTurnSnapshotTests
{
    private static (TuiSessionRenderer Renderer, StringBuilder Output) Create()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var writer = new StringWriter();
        return (new TuiSessionRenderer(writer), writer.GetStringBuilder());
    }

    private static IReadOnlyList<string> Rows(StringBuilder output) =>
        output.ToString()
            .Replace("\r", string.Empty)
            .Replace("\u001b[2K", string.Empty)
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .ToList();

    private static SseFrame Transient(string kind, object data, long sequence)
    {
        var inner = JsonSerializer.Serialize(data);
        return TransientRaw(kind, inner, sequence);
    }

    /// <summary>Transient frame whose <c>data</c> is already the inner JSON text.</summary>
    private static SseFrame TransientRaw(string kind, string innerJson, long sequence)
    {
        var outer = JsonSerializer.Serialize(new { kind, data = innerJson });
        return new SseFrame(null, "transient", outer, true, sequence);
    }

    private static SseFrame Delta(string text, long sequence) =>
        Transient(
            "assistant.delta",
            new { text, model_step_id = "step_" + new string('a', 32) },
            sequence);

    private static SseFrame Activity(
        string kind,
        string state,
        long sequence,
        string? fileName = null,
        string? symbol = null,
        int? lineStart = null,
        int? lineEnd = null,
        int? added = null,
        int? removed = null,
        int? durationMs = null,
        string? toolDisplay = null,
        string safeCode = "succeeded")
    {
        var payload = new Dictionary<string, object>
        {
            // A tool keeps its identity across start/completion, independently of others.
            ["activity_id"] = "act_" + kind,
            ["kind"] = kind,
            ["state"] = state,
            ["safe_code"] = safeCode,
        };
        if (fileName is not null) payload["file_name"] = fileName;
        if (symbol is not null) payload["symbol"] = symbol;
        if (lineStart is not null) payload["line_start"] = lineStart.Value;
        if (lineEnd is not null) payload["line_end"] = lineEnd.Value;
        if (added is not null) payload["added"] = added.Value;
        if (removed is not null) payload["removed"] = removed.Value;
        if (durationMs is not null) payload["duration_ms"] = durationMs.Value;
        if (toolDisplay is not null) payload["tool_display"] = toolDisplay;

        return Transient("agent.activity", payload, sequence);
    }

    private static SseFrame Durable(string eventType, string status, string safeSummary, long sequence) =>
        new(
            sequence,
            "message",
            JsonSerializer.Serialize(new HarnessSafeEventEnvelope(
                ProtocolVersion: 1,
                EventId: "evt_" + sequence,
                RunId: "run_" + new string('c', 32),
                SessionId: "sess_1",
                Sequence: sequence,
                EventType: eventType,
                OccurredAt: "2026-09-10T00:00:00Z",
                Status: status,
                SafeSummary: safeSummary)),
            false,
            null);

    /// <summary>The scripted turn every assertion below shares.</summary>
    private static (StringBuilder Output, TuiSessionRenderer Renderer) PlayTurn()
    {
        var (renderer, output) = Create();
        renderer.Width = 80;
        renderer.BeginTurn();

        renderer.RenderFrame(Activity("thinking", "started", 1));
        renderer.RenderFrame(Delta("Để em xem renderer trước nhé.\n", 2));

        renderer.RenderFrame(Activity("analyze", "started", 3, fileName: "TuiEventRenderer.cs", toolDisplay: "View"));
        renderer.RenderFrame(Durable("tool.execution_requested", "running", "read_only", 4));
        renderer.RenderFrame(Activity("analyze", "completed", 5, fileName: "TuiEventRenderer.cs", durationMs: 12, toolDisplay: "View"));
        renderer.RenderFrame(Durable("tool.execution_completed", "succeeded", "read_only", 6));

        renderer.RenderFrame(Activity("edit", "completed", 7, fileName: "AgentCommands.cs", lineStart: 12, lineEnd: 40, added: 12, removed: 4, durationMs: 340, toolDisplay: "Edit"));
        renderer.RenderFrame(Durable("tool.execution_completed", "succeeded", "side_effects_possible", 8));

        renderer.RenderFrame(TransientRaw(
            "agent.summary",
            AgentRunSummaryWireCodec.Serialize(new AgentRunSummaryWire("succeeded", "verified", "LLM", 40, 3, 1, 0)),
            9));
        renderer.RenderFrame(Durable("run.state_changed", "completed", "completed", 10));
        renderer.EndTurn();
        return (output, renderer);
    }

    [Fact]
    public void Turn_PrintsOneRowPerSettledToolWithItsDetail()
    {
        var (output, _) = PlayTurn();

        var toolRows = Rows(output)
            .Where(row => row.Contains('✓') && (row.Contains("Read", StringComparison.Ordinal) || row.Contains("Edit", StringComparison.Ordinal)))
            .ToList();

        Assert.Equal(2, toolRows.Count);

        var readRow = toolRows[0];
        Assert.Contains("✓ Read", readRow, StringComparison.Ordinal);
        Assert.Contains("TuiEventRenderer.cs", readRow, StringComparison.Ordinal);
        Assert.Contains("12ms", readRow, StringComparison.Ordinal);

        var editRow = toolRows[1];
        Assert.Contains("✓ Edit", editRow, StringComparison.Ordinal);
        Assert.Contains("AgentCommands.cs:12-40", editRow, StringComparison.Ordinal);
        Assert.Contains("+12 −4", editRow, StringComparison.Ordinal);
        Assert.Contains("340ms", editRow, StringComparison.Ordinal);
    }

    [Fact]
    public void Turn_NeverRepeatsAToolRowForRequestAndCompletion()
    {
        var (output, _) = PlayTurn();

        var rows = Rows(output);
        Assert.Single(rows, row => row.Contains("Read", StringComparison.Ordinal) && row.Contains("TuiEventRenderer.cs", StringComparison.Ordinal));
        Assert.Single(rows, row => row.Contains("Edit", StringComparison.Ordinal) && row.Contains("AgentCommands.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Turn_KeepsTheScriptedOrderOfProseToolsAndFooter()
    {
        var (output, _) = PlayTurn();

        var rows = Rows(output);
        var indexOf = new Func<string, int>(needle => rows.ToList().FindIndex(row => row.Contains(needle, StringComparison.Ordinal)));

        var prose = indexOf("Để em xem renderer trước nhé.");
        var read = indexOf("✓ Read");
        var edit = indexOf("✓ Edit");
        var footer = rows.ToList().FindLastIndex(row => row.Contains("✓", StringComparison.Ordinal) && row.Contains("2 tools", StringComparison.Ordinal));

        Assert.DoesNotContain(rows, row => row.Contains("◆", StringComparison.Ordinal));
        Assert.True(prose >= 0, "prose missing");
        Assert.True(prose < read, "the first tool row must follow the prose");
        Assert.True(read < edit, "tool rows keep their order");
        Assert.True(edit < footer, "the footer closes the turn");
    }

    [Fact]
    public void Turn_FooterCarriesDurationToolsFilesAndVerification()
    {
        var (output, _) = PlayTurn();

        var footer = Rows(output).Single(row => row.Contains("✓", StringComparison.Ordinal) && row.Contains("2 tools", StringComparison.Ordinal));

        Assert.Contains("2 tools", footer, StringComparison.Ordinal);
        Assert.Contains("3 files changed", footer, StringComparison.Ordinal);
        Assert.Contains("1 created", footer, StringComparison.Ordinal);
        Assert.Contains("verified", footer, StringComparison.Ordinal);
    }

    [Fact]
    public void Turn_EveryRowStaysWithinTheViewportWidth()
    {
        var (output, _) = PlayTurn();

        Assert.All(Rows(output), row => Assert.True(
            TuiText.VisibleWidth(row) <= 80,
            $"row wider than the viewport ({TuiText.VisibleWidth(row)}): {row}"));
    }

    [Fact]
    public void Turn_WithoutTransientDetailStillReportsTheSafeCode()
    {
        var (renderer, output) = Create();
        renderer.Width = 80;
        renderer.BeginTurn();
        renderer.RenderFrame(Durable("tool.execution_completed", "succeeded", "read_only", 5));
        renderer.RenderFrame(Durable("run.state_changed", "completed", "completed", 6));
        renderer.EndTurn();

        var rows = Rows(output);
        Assert.Contains(rows, row => row.Contains("✓ Tool", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("read_only", StringComparison.Ordinal));
    }

    [Fact]
    public void Turn_WithAFailingToolPrintsTheFailureGlyph()
    {
        var (renderer, output) = Create();
        renderer.Width = 80;
        renderer.BeginTurn();
        renderer.RenderFrame(Activity("terminal", "started", 1, symbol: "dotnet", toolDisplay: "Terminal"));
        renderer.RenderFrame(Activity("terminal", "failed", 2, symbol: "dotnet", toolDisplay: "Terminal"));
        renderer.RenderFrame(Durable("tool.execution_completed", "failed", "exit_1", 2));
        renderer.EndTurn();

        // The display name leads the row, so it reads "✗ Run  dotnet".
        Assert.Contains(Rows(output), row =>
            row.Contains('✗')
            && row.Contains("Run", StringComparison.Ordinal)
            && row.Contains("dotnet", StringComparison.Ordinal));
    }
}
