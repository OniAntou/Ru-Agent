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
    public void Tool_line_keeps_width_with_accent_bar()
    {
        TuiPalette.Level = TuiColorLevel.TrueColor;
        try
        {
            var line = TuiTranscript.ComposeToolLine(ToolLineStatus.Ok, "read", "app.cs:12-40", "+12 −4 · 340ms", 100);
            Assert.Equal(100, TuiText.VisibleWidth(line));
            Assert.Contains(TuiPalette.Paint("▌", TuiToken.Line), line);
        }
        finally
        {
            TuiPalette.ResetLevel();
        }
    }

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
    public void ComposeFailure_RendersTitleCauseAndFix()
    {
        var lines = TuiTranscript.ComposeFailure(6.1, "provider_rate_limited");

        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain("turn", lines[0], StringComparison.Ordinal);
        Assert.Contains("6.1s", lines[0], StringComparison.Ordinal);
        Assert.Contains("provider_rate_limited", lines[0], StringComparison.Ordinal);
        Assert.Contains("429", lines[1], StringComparison.Ordinal);
        Assert.Contains("/model", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeFailure_FallsBackWithCodeAndLogPointer()
    {
        var lines = TuiTranscript.ComposeFailure(0.5, "some_new_code");

        Assert.Equal(3, lines.Count);
        Assert.Contains("some_new_code", lines[0], StringComparison.Ordinal);
        Assert.Contains("some_new_code", lines[1], StringComparison.Ordinal);
        Assert.Contains("/status", lines[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("provider_rate_limited", true)]
    [InlineData("approval_expired", true)]
    [InlineData("catalog_unauthorized", true)]
    [InlineData("provider_not_active", true)]
    [InlineData("provider_configuration_changed", true)]
    [InlineData("forbidden", true)]
    [InlineData("server_error", true)]
    [InlineData("http_error", true)]
    [InlineData("transport_error", true)]
    [InlineData("malformed_payload", true)]
    [InlineData("response_too_large", true)]
    [InlineData("vision_unsupported", true)]
    [InlineData("empty_model_reply", true)]
    [InlineData("catalog_rate_limited", true)]
    [InlineData("catalog_timeout", true)]
    [InlineData("catalog_unavailable", true)]
    [InlineData("catalog_http_error", true)]
    [InlineData("catalog_malformed", true)]
    [InlineData("catalog_too_large", true)]
    [InlineData("catalog_configuration_error", true)]
    [InlineData("worker_failed", true)]
    [InlineData("runtime_drive_failed", true)]
    [InlineData("repeated_tool_call", true)]
    [InlineData("prompt_contract_invalid", true)]
    [InlineData("run_not_runnable", true)]
    [InlineData("workspace_busy", true)]
    [InlineData("session_busy", true)]
    [InlineData("approval_stale", true)]
    [InlineData("reconcile_stale", true)]
    [InlineData("reconciliation_required", true)]
    [InlineData("provider_bootstrap_failed", true)]
    [InlineData("tool_failed", true)]
    [InlineData("harness_store_unavailable", true)]
    [InlineData("session_create_failed", true)]
    [InlineData("invalid_workspace", true)]
    [InlineData("invalid_session_id", true)]
    [InlineData("preset_mismatch", true)]
    [InlineData("compact_failed", true)]
    [InlineData("command_unknown", true)]
    [InlineData("run_failed", true)]
    [InlineData("model_failed", true)]
    [InlineData("model_timeout", true)]
    [InlineData("prompt_too_large", true)]
    [InlineData("model_unavailable", true)]
    [InlineData("provider_auth", true)]
    [InlineData("protocol_failure", true)]
    [InlineData("what_is_this", false)]
    public void Remedy_CoversTheKnownRecoveryPaths(string code, bool hasRemedy)
    {
        Assert.Equal(hasRemedy, TuiTranscript.Remedy(code) is not null);
    }

    [Fact]
    public void ComposeAssistantHeader_NamesTheRoleOnce()
    {
        var header = TuiText.Strip(TuiTranscript.ComposeAssistantHeader(new DateTime(2026, 9, 24, 8, 36, 0)));

        Assert.Contains("◆", header, StringComparison.Ordinal);
        Assert.Contains("Ru", header, StringComparison.Ordinal);
        Assert.Contains("08:36", header, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstDelta_PrintsTheAssistantHeaderOnce()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.AppendAssistant("hello ");
        transcript.AppendAssistant("world");

        var text = output.ToString();
        Assert.Contains("◆", text, StringComparison.Ordinal);
        Assert.Contains("Ru", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split('◆').Length - 1);
    }

    [Fact]
    public void Streamed_assistant_text_records_the_raw_deltas_for_gap_detection()
    {
        // The live hub has no replay buffer, so deltas published before the client
        // subscribes are gone. The raw text actually received is what a gap check has to
        // compare the run's durable assistant payload against.
        var (transcript, _) = Create();
        transcript.BeginTurn();
        transcript.AppendAssistant("or the web, and more");

        Assert.Equal("or the web, and more", transcript.StreamedAssistantText);
    }

    [Fact]
    public void Streamed_assistant_text_starts_empty_and_resets_each_turn()
    {
        var (transcript, _) = Create();
        transcript.BeginTurn();
        transcript.AppendAssistant("first turn");
        transcript.EndTurn(TuiTurnOutcome.Completed, null);

        transcript.BeginTurn();

        Assert.Equal(string.Empty, transcript.StreamedAssistantText);
    }

    [Fact]
    public void Repaint_replaces_a_truncated_turn_with_the_complete_answer()
    {
        // What the live hub actually costs: the deltas that carried the head were published
        // before the client subscribed and are gone, so the turn rendered only its tail. A
        // terminal cannot unprint, so the block is replaced in the surface's recorded lines
        // and the screen is rebuilt from them -- the path a resize already uses.
        const string full =
            "Hi! I'm ready to help you with your workspace — reading and editing files, running commands, and more.";
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.AppendAssistant("running commands, and more.");
        transcript.Flush();

        Assert.True(transcript.RepaintAssistantText(full));
        Assert.Equal(full, transcript.StreamedAssistantText);

        // The rebuilt screen must show the head again, from the recorded lines rather than
        // from a fresh append.
        var rebuilt = string.Join("\n", transcript.RepaintPreview());
        Assert.Contains("ready to help you with your workspace", rebuilt, StringComparison.Ordinal);
        Assert.Contains("running commands, and more", rebuilt, StringComparison.Ordinal);
        Assert.DoesNotContain("◆", rebuilt, StringComparison.Ordinal);
    }

    [Fact]
    public void Repaint_declines_when_nothing_was_streamed_this_turn()
    {
        var (transcript, _) = Create();
        transcript.BeginTurn();

        // Nothing streamed means there is no block to place the answer in; the caller appends
        // instead, and the repaint must not invent one.
        Assert.False(transcript.RepaintAssistantText("some text"));
    }

    [Fact]
    public void Repaint_marks_a_fresh_block_for_each_turn()
    {
        var (transcript, _) = Create();
        transcript.BeginTurn();
        transcript.AppendAssistant("first tail");
        transcript.Flush();
        Assert.True(transcript.RepaintAssistantText("first head and tail"));

        transcript.EndTurn(TuiTurnOutcome.Completed, null);
        transcript.BeginTurn();

        // The next turn must not try to repaint the previous turn's rows.
        Assert.False(transcript.RepaintAssistantText("second turn text"));
    }

    [Fact]
    public void Real_short_reply_from_the_host_renders_completely()
    {
        // The exact 162-character answer the host persisted, em dash and all. Three or four
        // wrapped lines cannot overflow a transcript area, so a turn that renders nothing at
        // all is not the lost-head scroll bug. If this passes, the loss is downstream of the
        // transcript: the pinned-footer paint, which a StringWriter cannot show because it
        // has no screen, no scrollback and no cursor.
        const string answer =
            "Hi! I'm ready to help you with your workspace — reading and editing files, "
            + "running commands, searching the web, or planning out a task. What would you "
            + "like to do?";
        var (transcript, output) = Create();
        transcript.BeginTurn();

        foreach (var piece in Chunk(answer, 11))
        {
            transcript.AppendAssistant(piece);
        }

        transcript.EndTurn(TuiTurnOutcome.Completed, null);

        var text = TuiText.Strip(string.Join(" ", Rows(output)));
        Assert.Contains("Hi! I'm ready to help you with your workspace", text, StringComparison.Ordinal);
        Assert.Contains("What would you like to do?", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no text arrived", text, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var offset = 0; offset < text.Length; offset += size)
        {
            yield return text.Substring(offset, Math.Min(size, text.Length - offset));
        }
    }

    [Fact]
    public void Completed_turn_with_no_text_and_no_tools_says_so_instead_of_ending_silent()
    {
        // A finished run that delivered nothing used to leave only "✓ idle · 10.6s",
        // which reads exactly like a very short answer. The operator has to be told.
        var (transcript, output) = Create();
        transcript.BeginTurn();

        transcript.EndTurn(TuiTurnOutcome.Completed, null);

        var rows = Rows(output);
        Assert.Contains(rows, row => row.Contains("no text arrived from the model"));
        Assert.Contains(rows, row => row.Contains("/model"));
    }

    [Fact]
    public void Completed_turn_that_streamed_text_adds_no_note()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.AppendAssistant("PONG");
        output.Clear();

        transcript.EndTurn(TuiTurnOutcome.Completed, null);

        Assert.DoesNotContain(Rows(output), row => row.Contains("no text arrived"));
    }

    [Fact]
    public void Completed_turn_with_only_tool_rows_adds_no_note()
    {
        // Work that did run is visible as tool rows, so silence there is not a blank turn.
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.ApplyDurable(Durable("tool.execution_completed", "succeeded", "read_only"));
        output.Clear();

        transcript.EndTurn(TuiTurnOutcome.Completed, null);

        Assert.DoesNotContain(Rows(output), row => row.Contains("no text arrived"));
    }

    [Fact]
    public void Failed_turn_keeps_its_failure_card_and_adds_no_note()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();

        transcript.EndTurn(TuiTurnOutcome.Failed, "empty_model_reply");

        var rows = Rows(output);
        Assert.Contains(rows, row => row.Contains("Empty model reply"));
        Assert.DoesNotContain(rows, row => row.Contains("no text arrived"));
    }

    [Fact]
    public void BuildFooter_JoinsFactsWithTheSummaryDetail()    {
        var footer = TuiTranscript.BuildFooter(18.4, 4, "3 files changed · verified");

        Assert.DoesNotContain("turn", footer, StringComparison.Ordinal);
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
        transcript.BeginTurn();
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
        transcript.BeginTurn();
        transcript.ApplyDurable(Durable("tool.execution_completed", "succeeded", "read_only"));

        var toolRow = Assert.Single(Rows(output), row => row.Contains('✓'));

        Assert.Contains("tool", toolRow, StringComparison.Ordinal);
    }

    [Fact]
    public void SettledTool_MarksFailuresFromEitherStream()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.ApplyActivity(Activity(AgentActivityKind.Terminal, AgentActivityState.Started, toolDisplay: "Terminal"));
        transcript.ApplyActivity(Activity(AgentActivityKind.Terminal, AgentActivityState.Failed, durationMs: 2300, toolDisplay: "Terminal"));
        transcript.ApplyDurable(Durable("tool.execution_completed", "failed", "exit_1"));

        Assert.Contains(Rows(output), row => row.Contains('✗') && row.Contains("ran", StringComparison.Ordinal));
    }

    [Fact]
    public void DurableCompletionWithoutASettledToolNeverLosesTheRecord()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.ApplyDurable(Durable("tool.execution_completed", "succeeded", "ok"));
        transcript.ApplyDurable(Durable("tool.execution_completed", "succeeded", "ok", sequence: 6));

        Assert.Equal(2, Rows(output).Count(row => row.Contains('✓')));
    }

    [Fact]
    public void DiffBlock_IsBoundedAndOffersExpansion()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
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
        transcript.BeginTurn();
        transcript.ApplySummary(new AgentRunSummaryWire("completed", "verified", "LLM", 40, 3, 1, 0));
        transcript.ApplyDurable(Durable("run.state_changed", "completed", "completed"));
        transcript.EndTurn();

        Assert.Contains(Rows(output), row =>
            row.Contains("3 files changed", StringComparison.Ordinal)
            && row.Contains("1 created", StringComparison.Ordinal)
            && row.Contains("verified", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedTurn_PrintsTheCodeAndRemedy()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.ApplyDurable(Durable("run.failed", "failed", "provider_rate_limited"));
        transcript.EndTurn();

        var rows = Rows(output);
        Assert.Contains(rows, row => row.Contains('✗') && row.Contains("provider_rate_limited", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("/model", StringComparison.Ordinal));
    }

    [Fact]
    public void AbortedTurn_StopsSilently()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.EndTurn();

        Assert.DoesNotContain(Rows(output), row => row.Contains('✓') || row.Contains('✗'));
    }

    [Fact]
    public void RunStateChanged_WhileRunningPrintsNoFooterAndNoNotice()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.ApplyDurable(Durable("run.state_changed", "running", "running"));
        transcript.EndTurn();

        var rows = Rows(output);
        Assert.DoesNotContain(rows, row => row.Contains("run.state_changed", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains('✓'));
    }

    [Fact]
    public void StreamedMarkdownReachesTheSurfaceStyled()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            var (transcript, output) = Create();
            transcript.BeginTurn();
            transcript.AppendAssistant("Fix applied:\n\n- first **bold** item\n- `second` item\n");
            transcript.Flush();

            var text = TuiText.Strip(output.ToString()).Replace("\r", string.Empty);
            Assert.DoesNotContain("**", text, StringComparison.Ordinal);
            Assert.DoesNotContain("`second`", text, StringComparison.Ordinal);
            Assert.Contains("•  first bold item", text, StringComparison.Ordinal);
            Assert.Contains("•  second item", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AFencedCodeBlockReachesTheSurfaceWithAGutterAndVerbatimContent()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            var (transcript, output) = Create();
            transcript.BeginTurn();
            transcript.AppendAssistant("Here:\n```csharp\nvar x = 1; // **not** bold\n```\nafter\n");
            transcript.Flush();

            var text = TuiText.Strip(output.ToString()).Replace("\r", string.Empty);
            Assert.Contains("var x = 1; // **not** bold", text, StringComparison.Ordinal);
            Assert.DoesNotContain("```", text, StringComparison.Ordinal);
            Assert.Contains("after", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AMarkerSplitAcrossDeltasNeverLosesOrDuplicatesText()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            var (transcript, output) = Create();
            transcript.BeginTurn();
            transcript.AppendAssistant("see **x");
            transcript.AppendAssistant("** and more");
            transcript.Flush();

            // Emphasis is best effort across a delta boundary, but the characters are not:
            // whatever the model said reaches the surface exactly once, in order.
            var text = TuiText.Strip(output.ToString()).Replace("\r", string.Empty);
            Assert.Contains("see **x** and more", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AWellFormedRunInOneDeltaIsRenderedNotPrinted()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            var (transcript, output) = Create();
            transcript.BeginTurn();
            transcript.AppendAssistant("a **bold** word\n");
            transcript.Flush();

            var text = TuiText.Strip(output.ToString()).Replace("\r", string.Empty);
            Assert.Contains("a bold word", text, StringComparison.Ordinal);
            Assert.DoesNotContain("**", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AssistantText_StreamsAndSwallowsToolMarkers()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
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
        transcript.BeginTurn();
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
        transcript.BeginTurn();
        transcript.ApplyActivity(Activity(AgentActivityKind.Thinking, AgentActivityState.Started));

        Assert.DoesNotContain(Rows(output), row => row.Contains("Thinking", StringComparison.Ordinal));
    }

    [Fact]
    public void ThinkingCompleted_StaysOutOfTheTranscriptUntilExpanded()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.ApplyActivity(new AgentActivityModel(
            "act_1",
            AgentActivityKind.Thinking,
            AgentActivityState.Completed,
            FileName: null,
            LineStart: null,
            LineEnd: null,
            Symbol: null,
            Count: null,
            Added: null,
            Removed: null,
            DurationMs: 1200,
            SafeCode: "succeeded",
            ToolDisplay: null,
            DurableSequence: null,
            TransientSequence: null,
            Detail: "considering folder scope",
            Todos: null));

        // The deliberation itself is never in the transcript; the row only says it is there.
        Assert.DoesNotContain(
            Rows(output),
            row => row.Contains("considering folder scope", StringComparison.Ordinal));
        Assert.Contains(Rows(output), row => row.Contains("ctrl+o", StringComparison.Ordinal));

        transcript.ToggleExpand();

        Assert.Contains(
            Rows(output),
            row => row.Contains("considering folder scope", StringComparison.Ordinal));
    }

    [Fact]
    public void BeginTurn_DoesNotEmitLegacyTurnHeader()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();

        var rows = Rows(output);
        Assert.DoesNotContain(rows, row => row.Contains("◆", StringComparison.Ordinal));
    }

    [Fact]
    public void BeginTurn_ResetsPerTurnState()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.ApplyDurable(Durable("run.failed", "failed", "boom"));
        transcript.BeginTurn();
        transcript.EndTurn();

        var all = Rows(output).ToList();
        Assert.DoesNotContain(all, row => row.Contains("boom", StringComparison.Ordinal));
        _ = Empty;
    }

    [Theory]
    [InlineData("tool.proposed")]
    [InlineData("continue.requested")]
    [InlineData("model.reasoning_recorded")]
    public void ApplyDurable_IgnoresInternalEvents(string eventType)
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.ApplyDurable(Durable(eventType, "running", "internal payload"));

        var rows = Rows(output);
        Assert.DoesNotContain(rows, row => row.Contains(eventType, StringComparison.Ordinal));
    }

    [Fact]
    public void AppendAssistant_AddsSpacingAfterSettledTool()
    {
        var (transcript, output) = Create();
        transcript.BeginTurn();
        transcript.ApplyDurable(Durable("tool.execution_completed", "succeeded", "read_only"));
        transcript.AppendAssistant("Hello there!");
        transcript.Flush();

        var text = TuiText.Strip(output.ToString()).Replace("\r", string.Empty);
        Assert.Contains("◆ Ru", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("◆ Ru", StringComparison.Ordinal) < text.IndexOf("Hello there!", StringComparison.Ordinal),
            "the assistant header must sit between the settled tool and the prose");
    }
}
