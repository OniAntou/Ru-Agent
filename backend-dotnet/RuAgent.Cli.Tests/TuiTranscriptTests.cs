using System.Text;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiTranscriptTests
{
    private static readonly string[] Empty = [];

    private static (TuiTranscript Transcript, StringBuilder Output) Create()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var writer = new StringWriter();
        var surface = new TuiSurface(writer);
        var status = new TuiStatusLine(surface);
        return (new TuiTranscript(surface, status) { Width = 80 }, writer.GetStringBuilder());
    }

    /// <summary>Transcript rows as plain lines: carriage-return repaints normalized away.</summary>
    private static IReadOnlyList<string> Rows(StringBuilder output) =>
        output.ToString()
            .Replace("\r", string.Empty)
            .Replace("\u001b[2K", string.Empty)
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .ToList();

    private static HarnessSafeEventEnvelope Durable(
        string eventType,
        string status,
        string safeSummary,
        long sequence = 5) =>
        new(
            ProtocolVersion: 1,
            EventId: "evt_1",
            RunId: "run_1",
            SessionId: "sess_1",
            Sequence: sequence,
            EventType: eventType,
            OccurredAt: "2026-09-10T00:00:00Z",
            Status: status,
            SafeSummary: safeSummary);

    private static AgentActivityModel Activity(
        AgentActivityKind kind,
        AgentActivityState state,
        string? fileName = null,
        int? lineStart = null,
        int? lineEnd = null,
        string? symbol = null,
        int? count = null,
        int? added = null,
        int? removed = null,
        int? durationMs = null,
        string safeCode = "succeeded",
        string? toolDisplay = null) =>
        new("act_1", kind, state, fileName, lineStart, lineEnd, symbol, count, added, removed, durationMs, safeCode, toolDisplay, null, null);

    [Fact]
    public void ComposeToolLine_FillsTheWidthAndAlignsColumns()
    {
        var line = TuiTranscript.ComposeToolLine(ToolLineStatus.Ok, "edit", "src/app.cs:12-40", "+12 -4 340ms", 80);

        Assert.Equal(80, TuiText.VisibleWidth(line));
        Assert.Contains("✓ edit", line, StringComparison.Ordinal);
        Assert.Contains("src/app.cs:12-40", line, StringComparison.Ordinal);
        Assert.Contains("+12 -4 340ms", line, StringComparison.Ordinal);
        Assert.True(line.IndexOf("src/app.cs", StringComparison.Ordinal) < line.IndexOf("+12", StringComparison.Ordinal));
    }

    [Fact]
    public void ComposeToolLine_UsesFailureGlyphAndDropsMetaWithoutRoom()
    {
        var failed = TuiTranscript.ComposeToolLine(ToolLineStatus.Failed, "bash", "dotnet test", "exit 1", 40);
        Assert.Contains("✗ bash", failed, StringComparison.Ordinal);

        var cramped = TuiTranscript.ComposeToolLine(ToolLineStatus.Ok, "read", "a-very-long-target-name", "+1 -1 9ms", 20);
        Assert.Equal(20, TuiText.VisibleWidth(cramped));
        Assert.DoesNotContain("9ms", cramped, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData(12, 4, null, "+12 -4")]
    [InlineData(0, 3, 340, "-3 340ms")]
    [InlineData(null, null, 340, "340ms")]
    [InlineData(1, null, 1500, "+1 1.5s")]
    public void ComposeToolMeta_JoinsOnlyThePresentParts(int? added, int? removed, int? durationMs, string? expected)
    {
        Assert.Equal(expected, TuiTranscript.ComposeToolMeta(added, removed, durationMs));
    }

    [Fact]
    public void ComposeFailure_AddsOneActionableRemedyLine()
    {
        var lines = TuiTranscript.ComposeFailure(3, 6.1, "provider_rate_limited");

        Assert.Equal(2, lines.Count);
        Assert.Contains("turn 3", lines[0], StringComparison.Ordinal);
        Assert.Contains("6.1s", lines[0], StringComparison.Ordinal);
        Assert.Contains("provider_rate_limited", lines[0], StringComparison.Ordinal);
        Assert.Contains("/model", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeFailure_StaysSingleLineForUnknownCodes()
    {
        Assert.Single(TuiTranscript.ComposeFailure(1, 0.5, "some_new_code"));
    }

    [Theory]
    [InlineData("provider_rate_limited", true)]
    [InlineData("approval_expired", true)]
    [InlineData("what_is_this", false)]
    public void Remedy_CoversTheKnownRecoveryPaths(string code, bool hasRemedy)
    {
        Assert.Equal(hasRemedy, TuiTranscript.Remedy(code) is not null);
    }

    [Fact]
    public void BuildFooter_JoinsTurnFactsWithTheSummaryDetail()
    {
        var footer = TuiTranscript.BuildFooter(3, 18.4, 4, "3 files changed · verified");

        Assert.Contains("turn 3", footer, StringComparison.Ordinal);
        Assert.Contains("18.4s", footer, StringComparison.Ordinal);
        Assert.Contains("4 tools", footer, StringComparison.Ordinal);
        Assert.Contains("3 files changed · verified", footer, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDiffLine_MarksAdditionsDeletionsAndHunks()
    {
        Assert.StartsWith("+", TuiTranscript.FormatDiffLine("+added"), StringComparison.Ordinal);
        Assert.StartsWith("-", TuiTranscript.FormatDiffLine("-removed"), StringComparison.Ordinal);
        Assert.Equal("@@ -1 +1 @@", TuiTranscript.FormatDiffLine("@@ -1 +1 @@"));
    }

    [Fact]
    public void SettledTool_UsesTheCapturedActivityDetail()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(1);
        transcript.ApplyActivity(Activity(AgentActivityKind.Edit, AgentActivityState.Started, "src/app.cs", 12, 40, added: 12, removed: 4, toolDisplay: "Edit"));
        transcript.ApplyActivity(Activity(AgentActivityKind.Edit, AgentActivityState.Completed, "src/app.cs", 12, 40, added: 12, removed: 4, durationMs: 340, toolDisplay: "Edit"));
        transcript.ApplyDurable(Durable("tool.execution_completed", "succeeded", "ok"));

        var rows = Rows(output);
        var toolRow = Assert.Single(rows, row => row.Contains('✓'));

        Assert.Contains("edit", toolRow, StringComparison.Ordinal);
        Assert.Contains("src/app.cs:12-40", toolRow, StringComparison.Ordinal);
        Assert.Contains("+12 -4 340ms", toolRow, StringComparison.Ordinal);
    }

    [Fact]
    public void SettledTool_DegradesToTheSafeCodeWhenNoActivityWasCaptured()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(1);
        transcript.ApplyDurable(Durable("tool.execution_completed", "succeeded", "read_only"));

        var toolRow = Assert.Single(Rows(output), row => row.Contains('✓'));

        Assert.Contains("tool", toolRow, StringComparison.Ordinal);
    }

    [Fact]
    public void SettledTool_MarksFailuresFromEitherStream()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(1);
        transcript.ApplyActivity(Activity(AgentActivityKind.Terminal, AgentActivityState.Started, toolDisplay: "Terminal"));
        transcript.ApplyActivity(Activity(AgentActivityKind.Terminal, AgentActivityState.Failed, durationMs: 2300, toolDisplay: "Terminal"));
        transcript.ApplyDurable(Durable("tool.execution_completed", "failed", "exit_1"));

        Assert.Contains(Rows(output), row => row.Contains('✗') && row.Contains("terminal", StringComparison.Ordinal));
    }

    [Fact]
    public void DurableCompletionWithoutASettledToolNeverLosesTheRecord()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(1);
        transcript.ApplyDurable(Durable("tool.execution_completed", "succeeded", "ok"));
        transcript.ApplyDurable(Durable("tool.execution_completed", "succeeded", "ok", sequence: 6));

        Assert.Equal(2, Rows(output).Count(row => row.Contains('✓')));
    }

    [Fact]
    public void DiffBlock_IsBoundedAndOffersExpansion()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(1);
        var diff = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"+line {i}"));
        transcript.ApplyDiffText(diff, 80);

        var rows = Rows(output);
        Assert.Equal(12, rows.Count(row => row.Contains("+line", StringComparison.Ordinal)));
        Assert.Contains(rows, row => row.Contains("8 more lines · ctrl+o", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("+line 19", StringComparison.Ordinal));

        transcript.ToggleExpand();

        var expanded = Rows(output);
        Assert.Contains(expanded, row => row.Contains("+line 19", StringComparison.Ordinal));
        Assert.Contains(expanded, row => row.Contains("end of expanded block", StringComparison.Ordinal));
    }

    [Fact]
    public void ToggleExpand_WithoutRetainedContentSaysSo()
    {
        var (transcript, output) = Create();
        transcript.ToggleExpand();

        Assert.Contains(Rows(output), row => row.Contains("nothing to expand", StringComparison.Ordinal));
    }

    [Fact]
    public void RunSummary_FeedsTheTurnFooter()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(3);
        transcript.ApplySummary(new AgentRunSummaryWire("completed", "verified", "LLM", 40, 3, 1, 0));
        transcript.ApplyDurable(Durable("run.completed", "succeeded", "ok"));
        transcript.EndTurn();

        Assert.Contains(Rows(output), row =>
            row.Contains("turn 3", StringComparison.Ordinal)
            && row.Contains("3 files changed", StringComparison.Ordinal)
            && row.Contains("1 created", StringComparison.Ordinal)
            && row.Contains("verified", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedTurn_PrintsTheCodeAndRemedy()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(3);
        transcript.ApplyDurable(Durable("run.failed", "failed", "provider_rate_limited"));
        transcript.EndTurn();

        var rows = Rows(output);
        Assert.Contains(rows, row => row.Contains("✗ turn 3", StringComparison.Ordinal) && row.Contains("provider_rate_limited", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("/model", StringComparison.Ordinal));
    }

    [Fact]
    public void AbortedTurn_StopsSilently()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(2);
        transcript.EndTurn();

        Assert.DoesNotContain(Rows(output), row => row.Contains('✓') || row.Contains('✗'));
    }

    [Fact]
    public void AssistantText_StreamsVerbatimAndSwallowsToolMarkers()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(1);
        transcript.AppendAssistant("checking now\n");
        transcript.AppendAssistant("<tool_call>{\"tool\":\"read\"}</tool_call>");
        transcript.Flush();

        var text = output.ToString();
        Assert.Contains("checking now", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<tool_call>", text, StringComparison.Ordinal);
        Assert.Contains("│", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Notices_RenderCompactionAndRetryAsSingleMutedRows()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(1);
        transcript.ApplyActivity(Activity(AgentActivityKind.Compaction, AgentActivityState.Completed, count: 12, durationMs: 1400));
        transcript.ApplyActivity(Activity(AgentActivityKind.Retry, AgentActivityState.Started, fileName: "README.md", count: 2));

        var rows = Rows(output);
        Assert.Contains(rows, row => row.Contains("context compacted · 12 items · 1.4s", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("retrying README.md", StringComparison.Ordinal));
    }

    [Fact]
    public void ThinkingActivity_DrivesTheLiveRowInsteadOfTheTranscript()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(1);
        transcript.ApplyActivity(Activity(AgentActivityKind.Thinking, AgentActivityState.Started));

        Assert.DoesNotContain(Rows(output), row => row.Contains("Thinking", StringComparison.Ordinal));
    }

    [Fact]
    public void BeginTurn_PrintsOneTurnHeader()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(7);

        var rows = Rows(output);
        Assert.Contains(rows, row => row.Contains("◆", StringComparison.Ordinal) && row.Contains("turn 7", StringComparison.Ordinal));
    }

    [Fact]
    public void BeginTurn_ResetsPerTurnState()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn(1);
        transcript.ApplyDurable(Durable("run.failed", "failed", "boom"));
        transcript.BeginTurn(2);
        transcript.EndTurn();

        Assert.DoesNotContain(Rows(output).SkipWhile(row => !row.Contains("turn 2", StringComparison.Ordinal)), row => row.Contains("boom", StringComparison.Ordinal));
        _ = Empty;
    }
}
