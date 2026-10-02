using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Cli;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiInteractiveTests
{
    [Fact]
    public void CliOutput_UsesTheAuroraPalette_WhenColourIsOn()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.Palette256))
        {
            var ok = TuiPalette.Ok("hello");
            Assert.Contains($"{TuiPalette.Code(TuiToken.Ok)}hello{TuiPalette.Reset}", ok, StringComparison.Ordinal);

            Assert.Contains(TuiPalette.Code(TuiToken.Err), TuiPalette.Err("boom"), StringComparison.Ordinal);
            Assert.Contains(TuiPalette.Code(TuiToken.Warn), TuiPalette.Warn("careful"), StringComparison.Ordinal);
            Assert.Contains(TuiPalette.Code(TuiToken.Brand), TuiPalette.Accent("/help"), StringComparison.Ordinal);
            Assert.Contains(TuiPalette.Code(TuiToken.Meta), TuiPalette.Quiet("meta"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CliOutput_IsPlain_WhenColourIsOff()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            Assert.Equal("hello", TuiPalette.Ok("hello"));
            Assert.Equal("hello", TuiPalette.BoldPaint("hello"));
            Assert.Equal("hello", TuiPalette.DimPaint("hello"));
            Assert.Equal("+ added line", TuiPalette.Ok("+ added line"));
        }
    }

    [Fact]
    public void TuiEventRenderer_RendersOneSettledToolLine()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            using var sw = new StringWriter();

            var request = new HarnessSafeEventEnvelope(
                ProtocolVersion: 1,
                EventId: "evt_1",
                RunId: "run_1",
                SessionId: "sess_1",
                Sequence: 1,
                EventType: "tool.execution_requested",
                OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
                Status: "running",
                SafeSummary: "read_only");
            TuiEventRenderer.RenderEnvelope(request, sw);

            var completion = new HarnessSafeEventEnvelope(
                ProtocolVersion: 1,
                EventId: "evt_2",
                RunId: "run_1",
                SessionId: "sess_1",
                Sequence: 2,
                EventType: "tool.execution_completed",
                OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
                Status: "succeeded",
                SafeSummary: "ok");
            TuiEventRenderer.RenderEnvelope(completion, sw);

            var output = sw.ToString();
            Assert.Contains("✓", output, StringComparison.Ordinal);
            Assert.Contains("tool", output, StringComparison.Ordinal);
            // The request line must never be echoed again by the completion.
            Assert.Single(output.Split('\n'), line => line.Contains('✓'));
        }
    }

    [Fact]
    public void TuiEventRenderer_RendersFailedTurnWithRemedy()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            using var sw = new StringWriter();
            var renderer = new TuiSessionRenderer(sw);
            renderer.BeginTurn();

            var failEnv = new HarnessSafeEventEnvelope(
                ProtocolVersion: 1,
                EventId: "evt_6",
                RunId: "run_1",
                SessionId: "sess_1",
                Sequence: 6,
                EventType: "run.failed",
                OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
                Status: "failed",
                SafeSummary: "provider_rate_limited");
            renderer.RenderEnvelope(failEnv);
            renderer.EndTurn();

            var output = sw.ToString();
            Assert.Contains("✗", output, StringComparison.Ordinal);
            Assert.DoesNotContain("turn ", output, StringComparison.Ordinal);
            Assert.Contains("provider_rate_limited", output, StringComparison.Ordinal);
            Assert.Contains("/model", output, StringComparison.Ordinal);
        }
    }

    private static SseFrame DeltaFrame(string text, long seq = 1) => new(
        null,
        "assistant.delta",
        JsonSerializer.Serialize(new
        {
            kind = "assistant.delta",
            data = JsonSerializer.Serialize(new
            {
                text,
                model_step_id = "step_0123456789abcdef0123456789abcdef",
            }),
        }),
        true,
        seq);

    [Fact]
    public void TuiSessionRenderer_StylesToolCallBlock_WithGutter()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            using var sw = new StringWriter();
            var renderer = new TuiSessionRenderer(sw);

            renderer.RenderFrame(DeltaFrame("<tool_call>\n", 1));
            renderer.RenderFrame(DeltaFrame("<tool_id>\n", 2));
            renderer.RenderFrame(DeltaFrame("{\"tool_id\": \"read\"}\n", 3));
            renderer.RenderFrame(DeltaFrame("</tool_call>\n", 4));

            var output = sw.ToString();
            Assert.DoesNotContain("✦", output, StringComparison.Ordinal);
            Assert.DoesNotContain("<tool_call", output, StringComparison.Ordinal);
            Assert.DoesNotContain("</tool_call>", output, StringComparison.Ordinal);
            Assert.Contains("│", output, StringComparison.Ordinal);
            Assert.Contains("<tool_id>", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TuiSessionRenderer_StylesInlineMarker_GluedToChatText()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            using var sw = new StringWriter();
            var renderer = new TuiSessionRenderer(sw);

            renderer.RenderFrame(DeltaFrame("Dạ, để em kiểm tra nhé!<tool_call>\n", 1));
            renderer.RenderFrame(DeltaFrame("<tool_id>\n<args>\n<op>list</op>\n</args>\n", 2));
            renderer.RenderFrame(DeltaFrame("</tool_call>\n", 3));

            var output = sw.ToString();
            Assert.Contains("Dạ, để em kiểm tra nhé!", output, StringComparison.Ordinal);
            Assert.DoesNotContain("<tool_call", output, StringComparison.Ordinal);
            Assert.DoesNotContain("</tool_call>", output, StringComparison.Ordinal);
            Assert.Contains("│", output, StringComparison.Ordinal);
            Assert.Contains("<tool_id>", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TuiSessionRenderer_StylesSplitMarker_AcrossChunks()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            using var sw = new StringWriter();
            var renderer = new TuiSessionRenderer(sw);

            renderer.RenderFrame(DeltaFrame("<tool_c", 1));
            renderer.RenderFrame(DeltaFrame("all>\n<tool_id>\n", 2));
            renderer.RenderFrame(DeltaFrame("</tool_call>\n", 3));

            var output = sw.ToString();
            Assert.DoesNotContain("<tool_c", output, StringComparison.Ordinal);
            Assert.DoesNotContain("all>", output, StringComparison.Ordinal);
            Assert.Contains("│", output, StringComparison.Ordinal);
            Assert.Contains("<tool_id>", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TuiSessionRenderer_StreamsNormalText_Verbatim()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            using var sw = new StringWriter();
            var renderer = new TuiSessionRenderer(sw);

            renderer.RenderFrame(DeltaFrame("Hello, ", 1));
            renderer.RenderFrame(DeltaFrame("world!\n", 2));

            var output = sw.ToString();
            Assert.Contains("Hello, world!", output, StringComparison.Ordinal);
            Assert.DoesNotContain("│", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TuiSessionRenderer_FlushesRemainder_OnDurableEnvelope()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            using var sw = new StringWriter();
            var renderer = new TuiSessionRenderer(sw);

            renderer.RenderFrame(DeltaFrame("tail-no-newline", 1));
            var env = new HarnessSafeEventEnvelope(
                ProtocolVersion: 1,
                EventId: "evt_9",
                RunId: "run_1",
                SessionId: "sess_1",
                Sequence: 9,
                EventType: "reconciliation.required",
                OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
                Status: "running",
                SafeSummary: "recovery_ambiguous");
            renderer.RenderEnvelope(env);

            var output = sw.ToString();
            Assert.Contains("tail-no-newline", output, StringComparison.Ordinal);
            Assert.Contains("recovery_ambiguous", output, StringComparison.Ordinal);
            Assert.True(
                output.IndexOf("tail-no-newline", StringComparison.Ordinal) < output.IndexOf("recovery_ambiguous", StringComparison.Ordinal),
                "buffered tail must print before the envelope output");
        }
    }

    [Fact]
    public void TuiSessionRenderer_RendersCompactionAndRetryActivities()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            using var sw = new StringWriter();
            var renderer = new TuiSessionRenderer(sw);
            renderer.BeginTurn();

            renderer.RenderFrame(ActivityFrame(AgentActivityKind.Compaction, AgentActivityState.Completed, count: 12, durationMs: 1400));
            renderer.RenderFrame(ActivityFrame(AgentActivityKind.Retry, AgentActivityState.Started, fileName: "README.md"));

            var output = sw.ToString();
            Assert.Contains("context compacted", output, StringComparison.Ordinal);
            Assert.Contains("12 items", output, StringComparison.Ordinal);
            Assert.Contains("1.4s", output, StringComparison.Ordinal);
            Assert.Contains("retrying README.md", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TuiSessionRenderer_ToolActivityDrivesTheLiveRowNotTheTranscript()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            using var sw = new StringWriter();
            var renderer = new TuiSessionRenderer(sw);
            renderer.BeginTurn();

            renderer.RenderFrame(ActivityFrame(AgentActivityKind.Analyze, AgentActivityState.Started, fileName: "app.cs"));
            renderer.RenderFrame(ActivityFrame(AgentActivityKind.Analyze, AgentActivityState.Completed, fileName: "app.cs", durationMs: 120));
            renderer.RenderEnvelope(new HarnessSafeEventEnvelope(
                ProtocolVersion: 1,
                EventId: "evt_7",
                RunId: "run_1",
                SessionId: "sess_1",
                Sequence: 7,
                EventType: "tool.execution_completed",
                OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
                Status: "succeeded",
                SafeSummary: "read_only"));

            var toolRow = Assert.Single(sw.ToString().Split('\n'), row => row.Contains('✓'));
            Assert.Contains("app.cs", toolRow, StringComparison.Ordinal);
            Assert.Contains("120ms", toolRow, StringComparison.Ordinal);
        }
    }

    private static SseFrame ActivityFrame(
        AgentActivityKind kind,
        AgentActivityState state,
        string? fileName = null,
        int? count = null,
        int? durationMs = null,
        string? toolDisplay = null) => new(
        null,
        "agent.activity",
        JsonSerializer.Serialize(new
        {
            kind = "agent.activity",
            data = JsonSerializer.Serialize(new
            {
                activity_id = "act_1234567890abcdef1234567890abcdef",
                kind = kind.ToString().ToLowerInvariant(),
                state = state.ToString().ToLowerInvariant(),
                file_name = fileName,
                count,
                duration_ms = durationMs,
                safe_code = "succeeded",
                tool_display = toolDisplay,
            }),
        }),
        true,
        1);

    [Fact]
    public void TuiEventRenderer_FiltersInternalUsageRecordedEvents()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.None))
        {
            using var sw = new StringWriter();
            var env = new HarnessSafeEventEnvelope(
                ProtocolVersion: 1,
                EventId: "evt_7",
                RunId: "run_1",
                SessionId: "sess_1",
                Sequence: 6,
                EventType: "model.usage_recorded",
                OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
                Status: "reported",
                SafeSummary: "reported");

            TuiEventRenderer.RenderEnvelope(env, sw);
            Assert.Equal(string.Empty, sw.ToString());
        }
    }

    private static SseFrame ActivityFrame(string innerJson, long seq = 1) => new(
        null,
        "transient",
        JsonSerializer.Serialize(new
        {
            kind = "agent.activity",
            data = innerJson,
        }),
        true,
        seq);

    [Fact]
    public void Parser_ParsesChatAndReplCommands()
    {
        var res1 = AgentCliParser.Parse(["agent", "chat", "--workspace", "C:\\temp", "--preset", "standard"]);
        Assert.True(res1.Success);
        var cmd1 = Assert.IsType<ChatCommand>(res1.Command);
        Assert.Equal("C:\\temp", cmd1.WorkspacePath);
        Assert.Equal("standard", cmd1.Preset);
        Assert.False(cmd1.Json);

        var res2 = AgentCliParser.Parse(["chat", "--workspace", "D:\\repo", "--session", "sess-custom"]);
        Assert.True(res2.Success);
        var cmd2 = Assert.IsType<ChatCommand>(res2.Command);
        Assert.Equal("D:\\repo", cmd2.WorkspacePath);
        Assert.Equal("sess-custom", cmd2.SessionId);

        var res3 = AgentCliParser.Parse(["repl", "--preset", "full", "--confirm-full-access"]);
        Assert.True(res3.Success);
        var cmd3 = Assert.IsType<ChatCommand>(res3.Command);
        Assert.Equal("full_access", cmd3.Preset);
    }

    [Fact]
    public void Parser_ClassicSwitchesChatToTheSequentialLayout()
    {
        var plain = AgentCliParser.Parse(["chat"]);
        Assert.True(plain.Success);
        Assert.False(Assert.IsType<ChatCommand>(plain.Command).Classic);

        var classic = AgentCliParser.Parse(["chat", "--classic"]);
        Assert.True(classic.Success);
        Assert.True(Assert.IsType<ChatCommand>(classic.Command).Classic);

        var duplicate = AgentCliParser.Parse(["chat", "--classic", "--classic"]);
        Assert.False(duplicate.Success);
        Assert.Equal("duplicate_switch", duplicate.ErrorCode);
    }

    [Fact]
    public void Parser_ParsesSessionSubcommands()
    {
        // session list
        var resList = AgentCliParser.Parse(["session", "list", "--json"]);
        Assert.True(resList.Success);
        var cmdList = Assert.IsType<SessionListCommand>(resList.Command);
        Assert.True(cmdList.Json);

        // session rewind
        var resRewind = AgentCliParser.Parse(["session", "rewind", "sess_parent_123", "--index", "1"]);
        Assert.True(resRewind.Success);
        var cmdRewind = Assert.IsType<SessionRewindCommand>(resRewind.Command);
        Assert.Equal("sess_parent_123", cmdRewind.SessionId);
        Assert.Equal(1, cmdRewind.ToMessageIndex);
    }

    [Fact]
    public void Parser_FailsOnInvalidSessionCommands()
    {
        // missing session id
        var res1 = AgentCliParser.Parse(["session", "rewind"]);
        Assert.False(res1.Success);
        Assert.Equal("missing_session_id", res1.ErrorCode);

        // missing index for rewind
        var res2 = AgentCliParser.Parse(["session", "rewind", "sess_parent_123"]);
        Assert.False(res2.Success);
        Assert.Equal("missing_index", res2.ErrorCode);

        // unknown subcommand
        var res3 = AgentCliParser.Parse(["session", "destroy"]);
        Assert.False(res3.Success);
        Assert.Equal("unknown_subcommand", res3.ErrorCode);
    }

    [Fact]
    public void InteractiveLineReader_AutoCompletes_NewSlashCommands()
    {
        string[] cmds = ["/help", "/model", "/backend", "/rewind", "/tree", "/stats", "/tokens", "/export", "/status", "/history", "/clear", "/exit", "/quit"];
        Assert.Equal("/tree", InteractiveLineReader.AutoComplete("/tr", cmds));
        Assert.Null(InteractiveLineReader.AutoComplete("/stat", cmds)); // ambiguous with /status
        Assert.Equal("/stats", InteractiveLineReader.AutoComplete("/stats", cmds));
        Assert.Equal("/tokens", InteractiveLineReader.AutoComplete("/tok", cmds));
        Assert.Equal("/export", InteractiveLineReader.AutoComplete("/exp", cmds));
    }

    [Fact]
    public void InteractiveLineReader_BoxRendering_UsesWidthProperly()
    {
        int width = 80;
        var top = InteractiveLineReader.BuildTop(width, false);
        var mid = InteractiveLineReader.BuildMid(width, "❯ ", "hello", false);
        var bottom = InteractiveLineReader.BuildBottom(width, "model-test", false);

        Assert.Contains("╭", top);
        Assert.Contains("╮", top);
        Assert.Contains("│ ❯ hello", mid);
        Assert.Contains("╰", bottom);
        Assert.Contains("model-test", bottom);

        int computedWidth = InteractiveLineReader.ComputeBoxWidth();
        Assert.True(computedWidth >= 10);
    }

    [Fact]
    public void InteractiveLineReader_BuildMid_PreventsBorderBreak_OnLongInput()
    {
        int width = 40;
        string longInput = new string('x', 100);
        var mid = InteractiveLineReader.BuildMid(width, "❯ ", longInput, false);
        var stripped = TuiText.Strip(mid);

        Assert.Equal(width, stripped.Length);
        Assert.StartsWith("│ ❯ ", stripped, StringComparison.Ordinal);
        Assert.EndsWith(" │", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveLineReader_BuildBottom_PreventsBorderBreak_OnLongLegend()
    {
        int width = 40;
        string longLegend = new string('L', 100);
        var bottom = InteractiveLineReader.BuildBottom(width, longLegend, false);
        var stripped = TuiText.Strip(bottom);

        Assert.Equal(width, stripped.Length);
        Assert.StartsWith("╰", stripped, StringComparison.Ordinal);
        Assert.EndsWith("╯", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void TuiEventRenderer_RendersDiffsUnderTheSettledToolLine()
    {
        TuiPalette.Level = TuiColorLevel.None;
        using var sw = new StringWriter();

        var doneEnv = new HarnessSafeEventEnvelope(
            ProtocolVersion: 1,
            EventId: "evt_done",
            RunId: "run_1",
            SessionId: "sess_1",
            Sequence: 11,
            EventType: "tool.execution_completed",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
            Status: "succeeded",
            SafeSummary: "--- a/file.cs\n+++ b/file.cs\n@@ -1,2 +1,2 @@\n-old line\n+new line");

        TuiEventRenderer.RenderEnvelope(doneEnv, sw);

        var output = sw.ToString();
        Assert.Contains("✓", output, StringComparison.Ordinal);
        Assert.Contains("@@ -1,2 +1,2 @@", output, StringComparison.Ordinal);
        Assert.Contains("+new line", output, StringComparison.Ordinal);
        Assert.Contains("-old line", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_WithTrustFlagApprovesAndResumes()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('a', 32);
        var trustReqId = "tr_" + new string('b', 32);
        var displayHash = new string('c', 64);
        var calls = new List<string>();
        long requestVersion = -1;
        bool trustCalled = false;
        bool cancelCalled = false;
        bool restoreCalled = false;

        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs/") && uri.EndsWith("/cancel"))
            {
                cancelCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"cancelled\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/sessions/new"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"session_id\":\"sess_123\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                calls.Add("preset");
                using var document = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\",\"session_id\":\"sess_123\",\"preset\":null,\"version\":0,\"restored\":true,\"replayed\":false}", Encoding.UTF8, "application/json") };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = "sess_123", preset = "standard", version = 1 }), Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs") && !uri.Contains("/trust") && !uri.EndsWith("/cancel"))
            {
                calls.Add("run");
                using var document = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                var versionElement = document.RootElement.GetProperty("preset_selection_version");
                requestVersion = versionElement.ValueKind == JsonValueKind.Number
                    ? versionElement.GetInt64()
                    : -1;
                var runJson = JsonSerializer.Serialize(new
                {
                    run_id = runId,
                    session_id = "sess_123",
                    workspace_id = "ws_test",
                    status = "waiting_workspace_trust",
                    preset = "standard",
                    policy_snapshot_version = 1L,
                    policy_snapshot_sha256 = new string('d', 64),
                    version = 1L,
                    last_sequence = 0L,
                    created_at = DateTimeOffset.UtcNow.ToString("O"),
                    updated_at = DateTimeOffset.UtcNow.ToString("O"),
                    trust_request_id = trustReqId,
                    display_identity_hash = displayHash
                });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(runJson, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains($"/runs/{runId}/trust"))
            {
                trustCalled = true;
                var trustJson = JsonSerializer.Serialize(new
                {
                    run_id = runId,
                    trust_request_id = trustReqId,
                    status = "waiting_history"
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(trustJson, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                var sse = $"id: 1\ndata: {{\"protocol_version\":1,\"event_id\":\"evt_1\",\"run_id\":\"{runId}\",\"session_id\":\"sess_123\",\"sequence\":1,\"event_type\":\"run.completed\",\"occurred_at\":\"2026-08-24T00:00:00Z\",\"status\":\"succeeded\",\"safe_summary\":\"done\"}}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var inReader = new StringReader("hello\n/exit\n");
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var httpClient = new HttpClient(handler);

        var commands = new AgentCommands(session, httpClient, outWriter, errWriter, inReader);
        var cmd = new ChatCommand("C:\\untrusted\\workspace", "sess_123", "standard", false, TrustWorkspace: true);

        var exit = await commands.ChatAsync(cmd, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(trustCalled);
        Assert.False(restoreCalled);
        Assert.False(cancelCalled, "granting trust must let the run proceed, not cancel it");
        Assert.Equal(new[] { "preset", "run" }, calls);
        Assert.Equal(1, requestVersion);
        var outStr = outWriter.ToString();
        Assert.DoesNotContain("Trust this workspace?", outStr);
    }

    [Fact]
    public async Task InteractiveLineReader_OnScreenRefreshedAsync_CanBeAssignedAndInvoked()
    {
        var reader = new InteractiveLineReader();
        bool callbackCalled = false;
        reader.OnScreenRefreshedAsync = () =>
        {
            callbackCalled = true;
            return Task.CompletedTask;
        };

        Assert.NotNull(reader.OnScreenRefreshedAsync);
        await reader.OnScreenRefreshedAsync();
        Assert.True(callbackCalled);
    }

    [Fact]
    public async Task InteractiveLineReader_PushToBottomAsync_SafeOnRedirectedOutput()
    {
        using var sw = new StringWriter();
        await InteractiveLineReader.PushToBottomAsync(sw);
        Assert.NotNull(sw.ToString());
    }

    [Theory]
    // Màn hình trống (header ~7 dòng): không đệm, khung nằm ngay dưới nội dung.
    [InlineData(7, 30, 0)]
    [InlineData(1, 30, 0)]
    [InlineData(15, 30, 0)]
    // Output gần đầy màn hình (cách đáy <= 4 dòng): đệm nốt để ghim khung xuống đáy.
    [InlineData(24, 30, 2)]
    [InlineData(22, 30, 4)]
    [InlineData(25, 30, 1)]
    // Đã chạm/quá đáy hoặc không có console: không đệm.
    [InlineData(26, 30, 0)]
    [InlineData(40, 30, 0)]
    [InlineData(0, 30, 0)]
    [InlineData(-1, 30, 0)]
    public void InteractiveLineReader_ComputePushLines_PadsOnlyNearBottom(int cursorTop, int windowHeight, int expected)
    {
        Assert.Equal(expected, InteractiveLineReader.ComputePushLines(cursorTop, windowHeight));
    }

    [Fact]
    public void TuiSessionRenderer_BeginEndTurn_PrintsTheTurnHeader_WithoutHanging()
    {
        TuiPalette.Level = TuiColorLevel.None;
        using var sw = new StringWriter();
        var renderer = new TuiSessionRenderer(sw);

        renderer.BeginTurn();
        renderer.EndTurn();
        renderer.EndTurn(); // second stop must no-op safely

        Assert.DoesNotContain("◆", sw.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("turn", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveLineReader_BuildSubmitEcho_FormatsWithBrandBorderAndTimestamp()
    {
        TuiPalette.Level = TuiColorLevel.TrueColor;
        try
        {
            var echo = InteractiveLineReader.BuildSubmitEcho("sửa lỗi busy", "21:35");

            var stripped = TuiText.Strip(echo);
            Assert.Contains("▌", stripped, StringComparison.Ordinal);
            Assert.Contains("sửa lỗi busy", stripped, StringComparison.Ordinal);
            Assert.Contains("21:35", stripped, StringComparison.Ordinal);
            Assert.DoesNotContain("> ", stripped, StringComparison.Ordinal);
            Assert.DoesNotContain("You", stripped, StringComparison.Ordinal);
            Assert.DoesNotContain("╭", stripped, StringComparison.Ordinal);
            Assert.DoesNotContain("╰", stripped, StringComparison.Ordinal);
            Assert.Contains(TuiPalette.Code(TuiToken.Brand), echo, StringComparison.Ordinal);
            Assert.Contains(TuiPalette.Code(TuiToken.Meta), echo, StringComparison.Ordinal);
            Assert.Contains(TuiPalette.SurfaceBgCode(), echo, StringComparison.Ordinal);

            var lines = echo.Split('\n');
            Assert.Equal(3, lines.Length);
            Assert.Equal(lines[0], lines[2]);
        }
        finally
        {
            TuiPalette.ResetLevel();
        }
    }

    [Fact]
    public void InteractiveLineReader_BuildSubmitEcho_SupportsMultiline()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var echo = InteractiveLineReader.BuildSubmitEcho("a\nb", "10:00");

        var lines = echo.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("> a · 10:00", lines[0], StringComparison.Ordinal);
        Assert.Contains("  b", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("You", echo, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveLineReader_BuildSubmitEcho_SupportsMultiline_TrueColor()
    {
        TuiPalette.Level = TuiColorLevel.TrueColor;
        try
        {
            var echo = InteractiveLineReader.BuildSubmitEcho("a\nb", "10:00", width: 60);

            var lines = echo.Split('\n');
            Assert.Equal(4, lines.Length);
            Assert.Contains(TuiPalette.SurfaceBgCode(), lines[0]);
            Assert.Contains(TuiPalette.SurfaceBgCode(), lines[1]);
            Assert.Contains(TuiPalette.SurfaceBgCode(), lines[2]);
            Assert.Contains(TuiPalette.SurfaceBgCode(), lines[3]);
            Assert.Contains("a", lines[1]);
            Assert.Contains("b", lines[2]);
            Assert.Contains("10:00", lines[1]);
        }
        finally
        {
            TuiPalette.ResetLevel();
        }
    }

    [Fact]
    public void InteractiveLineReader_ResizeEchoLine_ExpandsToFullscreenWidth()
    {
        TuiPalette.Level = TuiColorLevel.TrueColor;
        try
        {
            var echo80 = InteractiveLineReader.BuildSubmitEcho("Hello", "22:26", width: 80);
            var lines80 = echo80.Split('\n');
            Assert.Equal(3, lines80.Length);
            Assert.All(lines80, l => Assert.Equal(80, TuiText.VisibleWidth(l)));

            // Operator fullscreens the window to 142 columns
            var resizedLines = lines80.Select(l => InteractiveLineReader.ResizeEchoLine(l, 142)).ToList();
            Assert.Equal(3, resizedLines.Count);
            Assert.All(resizedLines, l => Assert.Equal(142, TuiText.VisibleWidth(l)));

            var stripped = TuiText.Strip(resizedLines[1]);
            Assert.Contains("▌", stripped, StringComparison.Ordinal);
            Assert.Contains("Hello", stripped, StringComparison.Ordinal);
            Assert.Contains("22:26", stripped, StringComparison.Ordinal);
            Assert.Contains(TuiPalette.SurfaceBgCode(), resizedLines[1], StringComparison.Ordinal);
        }
        finally
        {
            TuiPalette.ResetLevel();
        }
    }

    [Fact]
    public void InteractiveLineReader_ResizeEchoLine_ShrinksDownCleanly()
    {
        TuiPalette.Level = TuiColorLevel.TrueColor;
        try
        {
            var echo140 = InteractiveLineReader.BuildSubmitEcho("sửa lỗi terminal", "15:30", width: 140);
            var lines140 = echo140.Split('\n');
            Assert.Equal(3, lines140.Length);
            Assert.All(lines140, l => Assert.Equal(140, TuiText.VisibleWidth(l)));

            // Operator shrinks the window down to 60 columns
            var resizedLines = lines140.Select(l => InteractiveLineReader.ResizeEchoLine(l, 60)).ToList();
            Assert.Equal(3, resizedLines.Count);
            Assert.All(resizedLines, l => Assert.Equal(60, TuiText.VisibleWidth(l)));

            var stripped = TuiText.Strip(resizedLines[1]);
            Assert.Contains("sửa lỗi terminal", stripped, StringComparison.Ordinal);
            Assert.Contains("15:30", stripped, StringComparison.Ordinal);
        }
        finally
        {
            TuiPalette.ResetLevel();
        }
    }

    [Fact]
    public void InteractiveLineReader_ResizeEchoLine_LeavesNormalProseUnchanged()
    {
        var prose = "✦ Hello! I'm your assistant.";
        var result = InteractiveLineReader.ResizeEchoLine(prose, 120);
        Assert.Equal(prose, result);
    }

    [Fact]
    public void InteractiveLineReader_BuildSubmitEcho_ShowsImageLabels()
    {
        TuiPalette.Level = TuiColorLevel.TrueColor;
        try
        {
            var echo = InteractiveLineReader.BuildSubmitEcho(
                "look at this", "21:35", width: 80, attachmentLabels: ["1: cat.png", "2: dog.jpg"]);

            var stripped = TuiText.Strip(echo);
            Assert.Contains("[image 1: cat.png]", stripped, StringComparison.Ordinal);
            Assert.Contains("[image 2: dog.jpg]", stripped, StringComparison.Ordinal);
            Assert.Contains(TuiPalette.SurfaceBgCode(), echo, StringComparison.Ordinal);
        }
        finally
        {
            TuiPalette.ResetLevel();
        }
    }

    [Fact]
    public void InteractiveLineReader_BuildSubmitEcho_MonoShowsImageLabels()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var echo = InteractiveLineReader.BuildSubmitEcho("hi", "10:00", attachmentLabels: ["1: a.png"]);

        Assert.Contains("[image 1: a.png]", echo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void InteractiveLineReader_BuildBoxOpen_AdaptsToVariousTerminalWidths(int width)
    {
        var boxOpen = InteractiveLineReader.BuildBoxOpen(width, "❯ ", "model-test", false);
        Assert.Contains("╭", boxOpen);
        Assert.Contains("╰", boxOpen);
        Assert.Contains("model-test", boxOpen);

        var top = InteractiveLineReader.BuildTop(width);
        Assert.StartsWith("╭", top);
        Assert.EndsWith("╮", top);
    }

    [Fact]
    public async Task ChatAsync_WithoutTrustFlagReturnsTrustRequiredInJsonMode()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('a', 32);
        var trustReqId = "tr_" + new string('b', 32);
        var displayHash = new string('c', 64);
        var calls = new List<string>();
        long requestVersion = -1;
        bool trustCalled = false;
        bool cancelCalled = false;
        bool restoreCalled = false;

        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs/") && uri.EndsWith("/cancel"))
            {
                cancelCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"cancelled\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/sessions/new"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"session_id\":\"sess_123\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                calls.Add("preset");
                using var document = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\",\"session_id\":\"sess_123\",\"preset\":null,\"version\":0,\"restored\":true,\"replayed\":false}", Encoding.UTF8, "application/json") };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = "sess_123", preset = "standard", version = 1 }), Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs") && !uri.Contains("/trust") && !uri.EndsWith("/cancel"))
            {
                calls.Add("run");
                using var document = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                var versionElement = document.RootElement.GetProperty("preset_selection_version");
                requestVersion = versionElement.ValueKind == JsonValueKind.Number
                    ? versionElement.GetInt64()
                    : -1;
                var runJson = JsonSerializer.Serialize(new
                {
                    run_id = runId,
                    session_id = "sess_123",
                    workspace_id = "ws_test",
                    status = "waiting_workspace_trust",
                    preset = "standard",
                    policy_snapshot_version = 1L,
                    policy_snapshot_sha256 = new string('d', 64),
                    version = 1L,
                    last_sequence = 0L,
                    created_at = DateTimeOffset.UtcNow.ToString("O"),
                    updated_at = DateTimeOffset.UtcNow.ToString("O"),
                    trust_request_id = trustReqId,
                    display_identity_hash = displayHash
                });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(runJson, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains($"/runs/{runId}/trust"))
            {
                trustCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                var sse = $"id: 1\ndata: {{\"protocol_version\":1,\"event_id\":\"evt_1\",\"run_id\":\"{runId}\",\"session_id\":\"sess_123\",\"sequence\":1,\"event_type\":\"run.completed\",\"occurred_at\":\"2026-08-24T00:00:00Z\",\"status\":\"succeeded\",\"safe_summary\":\"done\"}}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var httpClient = new HttpClient(handler);

        // inReader injects one chat line so ChatAsync reaches CreateRun; Json mode must fail closed.
        var inReader = new StringReader("hello\n");
        var commands = new AgentCommands(session, httpClient, outWriter, errWriter, inReader);
        var cmd = new ChatCommand("C:\\untrusted\\workspace", "sess_123", "standard", true);

        var exit = await commands.ChatAsync(cmd, CancellationToken.None);

        // The session survives an untrusted run: the run is deliberately left waiting for
        // the operator's grant, so the turn resumes when they give it.
        Assert.Equal(0, exit);
        Assert.False(trustCalled);
        Assert.False(cancelCalled);
        Assert.False(restoreCalled);
        // --json owns stdout: the human notice lands on stderr, so an NDJSON
        // consumer parsing stdout never sees a bare sentence. The in-composer ask
        // has no composer here, so nothing prompts and nothing is cancelled.
        var outStr = outWriter.ToString();
        Assert.DoesNotContain("workspace_trust_required", outStr);
        Assert.Contains("workspace_trust_required", errWriter.ToString());
        Assert.DoesNotContain("Trust this workspace?", outStr);
    }

    [Fact]
    public async Task ChatAsync_WithoutTrustFlagRestoresActualSelectionWithoutReadingTrustPrompt()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);
        var runId = "run_" + new string('a', 32);
        var trustReqId = "tr_" + new string('b', 32);
        var state = "plan";
        var cancelCalled = false;
        var restoreCalled = false;
        var eventsCalled = false;
        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs/") && uri.EndsWith("/cancel"))
            {
                cancelCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"cancelled\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    state = "plan";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\",\"session_id\":\"sess_123\",\"preset\":\"standard\",\"version\":1,\"restored\":true,\"replayed\":false}", Encoding.UTF8, "application/json") };
                }

                state = "full_access";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = "sess_123", preset = "full_access", version = 1 }), Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs") && !uri.Contains("/trust") && !uri.EndsWith("/cancel"))
            {
                var runJson = JsonSerializer.Serialize(new
                {
                    run_id = runId,
                    session_id = "sess_123",
                    workspace_id = "ws_test",
                    status = "waiting_workspace_trust",
                    preset = "full_access",
                    policy_snapshot_version = 1L,
                    policy_snapshot_sha256 = new string('d', 64),
                    version = 1L,
                    last_sequence = 0L,
                    created_at = DateTimeOffset.UtcNow.ToString("O"),
                    updated_at = DateTimeOffset.UtcNow.ToString("O"),
                    trust_request_id = trustReqId,
                    display_identity_hash = new string('e', 64)
                });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(runJson, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("id: 1\ndata: {}\n\n", Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });
        var reader = new CountingReader("hello\n");
        var commands = new AgentCommands(session, new HttpClient(handler), new StringWriter(), new StringWriter(), reader);

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\untrusted\\workspace", "sess_123", "full_access", true, TrustWorkspace: false, ConfirmFullAccess: true),
            CancellationToken.None);

        // Redirected input has no composer, so the trust ask cannot be shown and the run is
        // never granted. The turn ends on the bounded code instead of parking on the run:
        // attaching left the status row spinning "Thinking" over a run that could not reply.
        Assert.Equal(0, exit);
        Assert.False(restoreCalled);
        Assert.False(cancelCalled);
        Assert.Equal("full_access", state);
        Assert.True(reader.ReadCount > 1, "the chat session must survive an untrusted run");
        Assert.False(eventsCalled, "a run still waiting on trust must not be streamed");
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("failure")]
    public async Task ChatAsync_FullAccessTrustFailureRestoresActualSelection(string failureMode)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);
        var runId = "run_" + new string('a', 32);
        var state = "plan";
        var restoreCalled = false;
        var cancelCalled = false;
        var eventsCalled = false;
        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    state = "plan";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\",\"session_id\":\"sess_123\",\"preset\":\"standard\",\"version\":1,\"restored\":true,\"replayed\":false}", Encoding.UTF8, "application/json") };
                }

                state = "full_access";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = "sess_123", preset = "full_access", version = 1 }), Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs") && !uri.Contains("/trust") && !uri.EndsWith("/cancel"))
            {
                var runJson = JsonSerializer.Serialize(new
                {
                    run_id = runId,
                    session_id = "sess_123",
                    workspace_id = "ws_test",
                    status = "waiting_workspace_trust",
                    preset = "full_access",
                    policy_snapshot_version = 1L,
                    policy_snapshot_sha256 = new string('d', 64),
                    version = 1L,
                    last_sequence = 0L,
                    created_at = DateTimeOffset.UtcNow.ToString("O"),
                    updated_at = DateTimeOffset.UtcNow.ToString("O"),
                    trust_request_id = "tr_" + new string('b', 32),
                    display_identity_hash = new string('e', 64)
                });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(runJson, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs/") && uri.EndsWith("/cancel"))
            {
                cancelCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"cancelled\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains($"/agent/v1/runs/{runId}/trust"))
            {
                return failureMode == "mismatch"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"run_id\":\"run_other\",\"trust_request_id\":\"tr_other\",\"status\":\"waiting_history\"}", Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error_code\":\"trust_failed\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("id: 1\ndata: {}\n\n", Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });
        var reader = new CountingReader("hello\n");
        var commands = new AgentCommands(session, new HttpClient(handler), new StringWriter(), new StringWriter(), reader);

        var exit = await commands.ChatAsync(
            new ChatCommand("C:\\untrusted\\workspace", "sess_123", "full_access", true, TrustWorkspace: true, ConfirmFullAccess: true),
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(restoreCalled);
        Assert.False(cancelCalled);
        Assert.Equal("full_access", state);
        Assert.True(reader.ReadCount > 1, "the chat session must survive an untrusted run");
        Assert.False(eventsCalled, "a run whose trust grant failed must not be streamed");
    }

    [Fact]
    public async Task ChatAsync_InvalidCreateStatusRestoresBeforeStreaming()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);
        var state = "plan";
        var restoreCalled = false;
        var eventsCalled = false;
        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    state = "plan";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\",\"session_id\":\"sess_123\",\"preset\":\"standard\",\"version\":1,\"restored\":true,\"replayed\":false}", Encoding.UTF8, "application/json") };
                }

                state = "standard";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"session_id\":\"sess_123\",\"preset\":\"standard\",\"version\":1,\"snapshot_token\":\"sps_" + new string('a', 32) + "\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs"))
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"run_id\":\"run_" + new string('a', 32) + "\",\"session_id\":\"sess_123\",\"workspace_id\":\"ws_test\",\"status\":\"failed\",\"preset\":\"standard\",\"created_at\":\"2026-09-24T00:00:00Z\",\"updated_at\":\"2026-09-24T00:00:00Z\"}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("id: 1\ndata: {}\n\n", Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });
        var reader = new CountingReader("hello\n");
        var commands = new AgentCommands(session, new HttpClient(handler), new StringWriter(), new StringWriter(), reader);

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", true), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.True(restoreCalled);
        Assert.Equal("plan", state);
        Assert.Equal(1, reader.ReadCount);
        Assert.False(eventsCalled);
    }

    [Fact]
    public async Task ChatAsync_OutputFailureBeforeCreateRestoresBeforeStreaming()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);
        var state = "plan";
        var restoreCalled = false;
        var runCalled = false;
        var eventsCalled = false;
        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                {
                    restoreCalled = true;
                    state = "plan";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\",\"session_id\":\"sess_123\",\"preset\":\"plan\",\"version\":1,\"restored\":true,\"replayed\":false}", Encoding.UTF8, "application/json") };
                }

                state = "standard";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = "sess_123", preset = "standard", version = 1 }), Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs"))
            {
                runCalled = true;
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"run_id\":\"run_" + new string('a', 32) + "\",\"session_id\":\"sess_123\",\"workspace_id\":\"ws_test\",\"status\":\"running\",\"preset\":\"standard\",\"created_at\":\"2026-09-24T00:00:00Z\",\"updated_at\":\"2026-09-24T00:00:00Z\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("id: 1\ndata: {}\n\n", Encoding.UTF8, "text/event-stream") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });
        var reader = new CountingReader("hello\n");
        var commands = new AgentCommands(session, new HttpClient(handler), new ThrowOnWriteWriter(), new StringWriter(), reader);

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", true), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(restoreCalled);
        Assert.Equal("plan", state);
        Assert.False(runCalled);
        Assert.False(eventsCalled);
    }

    [Fact]
    public async Task ChatAsync_ReportsBlockingRun_WhenWorkspaceIsBusy()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var blockingRunId = "run_" + new string('e', 32);
        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/sessions/new"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"session_id\":\"sess_123\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\",\"session_id\":\"sess_123\",\"preset\":\"standard\",\"version\":1,\"restored\":true,\"replayed\":false}", Encoding.UTF8, "application/json") };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = "sess_123", preset = "standard", version = 1 }), Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs"))
            {
                var busyJson = JsonSerializer.Serialize(new
                {
                    error_code = "workspace_busy",
                    message = "Workspace busy.",
                    blocking_run_id = blockingRunId,
                    blocking_run_status = "waiting_approval",
                });
                return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(busyJson, Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var httpClient = new HttpClient(handler);

        var inReader = new StringReader("hello\n/exit\n");
        var commands = new AgentCommands(session, httpClient, outWriter, errWriter, inReader);
        var cmd = new ChatCommand("C:\\busy\\workspace", "sess_123", "standard", false);

        var exit = await commands.ChatAsync(cmd, CancellationToken.None);

        Assert.Equal(0, exit);
        var errStr = errWriter.ToString();
        Assert.Contains(blockingRunId, errStr, StringComparison.Ordinal);
        Assert.Contains("waiting_approval", errStr, StringComparison.Ordinal);
        Assert.Contains($"ru agent cancel {blockingRunId}", errStr, StringComparison.Ordinal);
        // The raw error envelope must never reach the transcript.
        Assert.DoesNotContain("error_code", errStr, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveLineReader_BuildBottom_NeverSplitsAnsi_OnLongStyledLegend()
    {
        int width = 40;
        string styledLegend = "\x1b[36m" + new string('L', 100) + "\x1b[0m";
        var bottom = InteractiveLineReader.BuildBottom(width, styledLegend, false);
        var stripped = TuiText.Strip(bottom);

        Assert.Equal(width, stripped.Length);
        int escCount = System.Text.RegularExpressions.Regex.Matches(bottom, "\x1b").Count;
        int seqCount = System.Text.RegularExpressions.Regex.Matches(bottom, "\x1b\\[[0-9;]*[a-zA-Z]").Count;
        Assert.Equal(escCount, seqCount);
    }

    [Fact]
    public async Task ChatAsync_CancelDuringStream_PostsCancelForTheRun()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('a', 32);
        var cancelUris = new List<string>();
        // Signalled when the first /events request is handled: the deterministic
        // point to cancel, instead of a wall-clock window.
        var eventsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = "sess_123", preset = "standard", version = 1 }), Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs") && !uri.Contains("/trust") && !uri.EndsWith("/cancel"))
            {
                var runJson = JsonSerializer.Serialize(new
                {
                    run_id = runId,
                    session_id = "sess_123",
                    workspace_id = "ws_test",
                    status = "running",
                    preset = "standard",
                    created_at = DateTimeOffset.UtcNow.ToString("O"),
                    updated_at = DateTimeOffset.UtcNow.ToString("O"),
                });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(runJson, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/cancel"))
            {
                cancelUris.Add(uri);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"cancelled\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventsStarted.TrySetResult();
                var content = new StreamContent(new BlockingStream());
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var commands = new AgentCommands(session, new HttpClient(handler), outWriter, errWriter, new StringReader("hello\n"));

        using var cts = new CancellationTokenSource();

        var chat = commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), cts.Token);
        await eventsStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        var exit = await chat;

        Assert.Equal(0, exit);
        var cancelUri = Assert.Single(cancelUris);
        Assert.EndsWith($"/agent/v1/runs/{runId}/cancel", cancelUri, StringComparison.Ordinal);
        Assert.Contains("interrupted", outWriter.ToString(), StringComparison.Ordinal);
        Assert.Contains(runId, outWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_CancelBeforeRunCreated_DoesNotPostCancel()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runCreateStarted = false;
        var cancelPosted = false;

        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
            {
                using var document = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                if (document.RootElement.GetProperty("kind").GetString() == "session_preset_restore")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\",\"session_id\":\"sess_123\",\"preset\":\"standard\",\"version\":1,\"restored\":true,\"replayed\":false}", Encoding.UTF8, "application/json") };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = "sess_123", preset = "standard", version = 1 }), Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs") && !uri.EndsWith("/cancel"))
            {
                runCreateStarted = true;
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/cancel"))
            {
                cancelPosted = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"cancelled\"}", Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var commands = new AgentCommands(session, new HttpClient(handler), new StringWriter(), new StringWriter(), new StringReader("hello\n"));
        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), cts.Token);

        Assert.True(runCreateStarted);
        Assert.False(cancelPosted, "cancelling while the create request is in flight must not cancel a run that was never created");
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task AttachAsync_ResumesStream_AfterTransportBreak()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('a', 32);
        var eventUris = new List<string>();
        var getRunCalls = 0;

        string DurableData(int seq, string summary) => JsonSerializer.Serialize(new
        {
            protocol_version = 1,
            event_id = $"evt_resume_{seq}",
            run_id = runId,
            session_id = "sess_123",
            sequence = seq,
            event_type = "run.state_changed",
            occurred_at = "2026-09-10T00:00:00Z",
            status = "running",
            safe_summary = summary,
        });

        string RunJson(string status, string? terminalAt, long lastSequence) => JsonSerializer.Serialize(new
        {
            run_id = runId,
            session_id = "sess_123",
            workspace_id = "ws_test",
            status,
            history_wait_kind = (string?)null,
            preset = "standard",
            policy_snapshot_version = 1,
            policy_snapshot_sha256 = new string('d', 64),
            version = 1L,
            last_sequence = lastSequence,
            created_at = "2026-09-10T00:00:00Z",
            updated_at = "2026-09-10T00:00:00Z",
            terminal_at = terminalAt,
            terminal_error_code = (string?)null,
        });

        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventUris.Add(uri);
                if (uri.Contains("after_sequence=0"))
                {
                    var frame1 = $"id: 1\ndata: {DurableData(1, "first-chunk")}\n\n";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new BreakingStream(Encoding.UTF8.GetBytes(frame1)))
                        {
                            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") },
                        },
                    };
                }
                var frame2 = $"id: 2\ndata: {DurableData(2, "second-chunk")}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(frame2, Encoding.UTF8, "text/event-stream") };
            }
            if (req.Method == HttpMethod.Get && uri.EndsWith(runId, StringComparison.Ordinal))
            {
                getRunCalls++;
                var terminal = getRunCalls > 1;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        RunJson(terminal ? "completed" : "running", terminal ? "2026-09-10T00:00:01Z" : null, terminal ? 2L : 1L),
                        Encoding.UTF8,
                        "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var httpClient = new HttpClient(handler);

        var commands = new AgentCommands(session, httpClient, outWriter, errWriter, new StringReader(string.Empty));
        var exit = await commands.AttachAsync(new AttachCommand(runId, true), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(2, eventUris.Count);
        Assert.Contains("after_sequence=1", eventUris[1], StringComparison.Ordinal);
        var outStr = outWriter.ToString();
        Assert.Contains("first-chunk", outStr, StringComparison.Ordinal);
        Assert.Contains("second-chunk", outStr, StringComparison.Ordinal);
        Assert.DoesNotContain("run_error", errWriter.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("stream_interrupted", errWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_ParkedRun_EndsTurnWithoutCancel_PrintsContinue()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('a', 32);
        var payloadRef = "payload_" + new string('e', 32);
        var cancelPosted = false;
        var payloadFetchPosted = false;

        string RunJson(string status, long lastSequence, string? terminalAt) => JsonSerializer.Serialize(new
        {
            run_id = runId,
            session_id = "sess_123",
            workspace_id = "ws_test",
            status,
            history_wait_kind = (string?)null,
            preset = "standard",
            policy_snapshot_version = 1,
            policy_snapshot_sha256 = new string('d', 64),
            version = 1L,
            last_sequence = lastSequence,
            created_at = "2026-09-30T00:00:00Z",
            updated_at = "2026-09-30T00:00:00Z",
            terminal_at = terminalAt,
            terminal_error_code = (string?)null,
        });

        // A parked run that already produced model steps carries payload refs, so the
        // turn's follow path reaches RecoverMissingAssistantTextAsync with the token the
        // park already cancelled. That is the branch that must still not cancel the run:
        // without a ref the stream ends cleanly and the guard is never exercised.
        var stepCompleted = JsonSerializer.Serialize(new
        {
            protocol_version = 1,
            event_id = "evt_step_1",
            run_id = runId,
            session_id = "sess_123",
            sequence = 1,
            event_type = "model.step_completed",
            occurred_at = "2026-09-30T00:00:00Z",
            status = "completed",
            safe_summary = "step_done",
            payload_ref = payloadRef,
        });

        var parked = JsonSerializer.Serialize(new
        {
            protocol_version = 1,
            event_id = "evt_park_1",
            run_id = runId,
            session_id = "sess_123",
            sequence = 2,
            event_type = "continue.requested",
            occurred_at = "2026-09-30T00:00:00Z",
            status = "waiting_user",
            safe_summary = "model_step_budget_exhausted",
        });

        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = "sess_123", preset = "standard", version = 1 }), Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs") && !uri.EndsWith("/cancel"))
            {
                var created = JsonSerializer.Serialize(new
                {
                    run_id = runId,
                    session_id = "sess_123",
                    workspace_id = "ws_test",
                    status = "running",
                    preset = "standard",
                    created_at = DateTimeOffset.UtcNow.ToString("O"),
                    updated_at = DateTimeOffset.UtcNow.ToString("O"),
                });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(created, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/cancel"))
            {
                cancelPosted = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"cancelled\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/payloads/"))
            {
                payloadFetchPosted = true;
                // The response headers arrive, then the body read blocks: the park already
                // cancelled the turn token, so the read surfaces the OCE that the parked
                // guard must survive. A pre-serialized body would complete synchronously
                // and never exercise that branch.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ParkedStream(Array.Empty<byte>()))
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                    },
                };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                var sse = $"id: 1\ndata: {stepCompleted}\n\nid: 2\ndata: {parked}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ParkedStream(Encoding.UTF8.GetBytes(sse)))
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") },
                    },
                };
            }
            if (req.Method == HttpMethod.Get && uri.EndsWith(runId, StringComparison.Ordinal))
            {
                // The parked run stays non-terminal, so the turn can only end because the
                // stream was released -- not because the run finished on its own.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(RunJson("waiting_user", 2, null), Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var commands = new AgentCommands(session, new HttpClient(handler), outWriter, errWriter, new StringReader("hello\n/exit\n"));

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(cancelPosted, "a parked run is the user's to resume; the turn must not cancel it");
        var output = outWriter.ToString();
        Assert.Contains("/continue", output, StringComparison.Ordinal);
        // The payload-ref recovery is what forces the OCE-parked path: the fetch is
        // dispatched and the body read observes the park's cancelled token.
        Assert.True(payloadFetchPosted, "the model.step_completed payload ref must reach recovery");
        // The transcript notice is the only parked guidance: the OCE-parked path must not
        // print a second, near-duplicate line.
        Assert.Equal(1, output.Split("run parked", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task ChatAsync_SlashContinue_ResumesParkedRunFromItsCursor()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('b', 32);
        var continuePosts = new List<string>();
        var eventUris = new List<string>();
        var runGets = 0;

        string RunJson(string status, long lastSequence, string? terminalAt) => JsonSerializer.Serialize(new
        {
            run_id = runId,
            session_id = "sess_123",
            workspace_id = "ws_test",
            status,
            history_wait_kind = (string?)null,
            preset = "standard",
            policy_snapshot_version = 1,
            policy_snapshot_sha256 = new string('d', 64),
            version = 1L,
            last_sequence = lastSequence,
            created_at = "2026-09-30T00:00:00Z",
            updated_at = "2026-09-30T00:00:00Z",
            terminal_at = terminalAt,
            terminal_error_code = (string?)null,
        });

        string Durable(int seq, string eventType, string status, string summary) => JsonSerializer.Serialize(new
        {
            protocol_version = 1,
            event_id = $"evt_{seq}",
            run_id = runId,
            session_id = "sess_123",
            sequence = seq,
            event_type = eventType,
            occurred_at = "2026-09-30T00:00:00Z",
            status,
            safe_summary = summary,
        });

        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = "sess_123", preset = "standard", version = 1 }), Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/continue"))
            {
                continuePosts.Add(uri);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs") && !uri.EndsWith("/cancel"))
            {
                var created = JsonSerializer.Serialize(new
                {
                    run_id = runId,
                    session_id = "sess_123",
                    workspace_id = "ws_test",
                    status = "running",
                    preset = "standard",
                    created_at = DateTimeOffset.UtcNow.ToString("O"),
                    updated_at = DateTimeOffset.UtcNow.ToString("O"),
                });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(created, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventUris.Add(uri);
                if (uri.Contains("after_sequence=0"))
                {
                    var sse = $"id: 1\ndata: {Durable(1, "continue.requested", "waiting_user", "model_step_budget_exhausted")}\n\n";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new ParkedStream(Encoding.UTF8.GetBytes(sse)))
                        {
                            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") },
                        },
                    };
                }

                var resumed =
                    $"id: 2\ndata: {Durable(2, "continue.requested", "running", "continued")}\n\n"
                    + $"id: 3\ndata: {Durable(3, "tool.execution_completed", "succeeded", "read_only")}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(resumed, Encoding.UTF8, "text/event-stream") };
            }
            if (req.Method == HttpMethod.Get && uri.EndsWith(runId, StringComparison.Ordinal))
            {
                runGets++;
                // First read is the parked turn's own terminal check; then the /continue
                // cursor read; then the resumed stream's terminal check.
                return runGets switch
                {
                    1 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RunJson("waiting_user", 1, null), Encoding.UTF8, "application/json") },
                    2 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RunJson("waiting_user", 1, null), Encoding.UTF8, "application/json") },
                    _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RunJson("completed", 3, "2026-09-30T00:00:02Z"), Encoding.UTF8, "application/json") },
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var commands = new AgentCommands(session, new HttpClient(handler), outWriter, errWriter, new StringReader("hello\n/continue\n"));

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Single(continuePosts);
        Assert.EndsWith($"/agent/v1/runs/{runId}/continue", continuePosts[0], StringComparison.Ordinal);
        Assert.Contains(eventUris, uri => uri.Contains("after_sequence=1", StringComparison.Ordinal));
        // The resumed turn followed the run from the parked cursor: its tool row rendered.
        Assert.Contains("read_only", outWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatWithSession_parkedRun_SetsContinuePath()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('d', 32);
        var continuePosts = new List<string>();
        var eventUris = new List<string>();
        var runGets = 0;

        string RunJson(string status, long lastSequence, string? terminalAt) => JsonSerializer.Serialize(new
        {
            run_id = runId,
            session_id = "sess_123",
            workspace_id = "ws_test",
            status,
            history_wait_kind = (string?)null,
            preset = "standard",
            policy_snapshot_version = 1,
            policy_snapshot_sha256 = new string('d', 64),
            version = 1L,
            last_sequence = lastSequence,
            created_at = "2026-09-30T00:00:00Z",
            updated_at = "2026-09-30T00:00:00Z",
            terminal_at = terminalAt,
            terminal_error_code = (string?)null,
        });

        string Durable(int seq, string eventType, string status, string summary) => JsonSerializer.Serialize(new
        {
            protocol_version = 1,
            event_id = $"evt_{seq}",
            run_id = runId,
            session_id = "sess_123",
            sequence = seq,
            event_type = eventType,
            occurred_at = "2026-09-30T00:00:00Z",
            status,
            safe_summary = summary,
        });

        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Get && uri.Contains("/agent/v1/sessions/sess_123/runs"))
            {
                var items = JsonSerializer.Serialize(new
                {
                    items = new[] { new { run_id = runId } },
                    next_cursor = (string?)null,
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(items, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/continue"))
            {
                continuePosts.Add(uri);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                eventUris.Add(uri);
                var sse =
                    $"id: 2\ndata: {Durable(2, "continue.requested", "running", "continued")}\n\n"
                    + $"id: 3\ndata: {Durable(3, "run.completed", "completed", "done")}\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }
            if (req.Method == HttpMethod.Get && uri.EndsWith(runId, StringComparison.Ordinal))
            {
                runGets++;
                // 1: session-open adoption; 2: the continue cursor read; 3: the resumed
                // stream's terminal check.
                return runGets switch
                {
                    1 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RunJson("waiting_user", 1, null), Encoding.UTF8, "application/json") },
                    2 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RunJson("waiting_user", 1, null), Encoding.UTF8, "application/json") },
                    _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RunJson("completed", 3, "2026-09-30T00:00:02Z"), Encoding.UTF8, "application/json") },
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var commands = new AgentCommands(session, new HttpClient(handler), outWriter, errWriter, new StringReader("Continue\n/exit\n"));

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        // The adopted parked run is what bare Continue resumes; the notice names the
        // same fallback the live park prints.
        var output = outWriter.ToString();
        Assert.Contains("run parked", output, StringComparison.Ordinal);
        Assert.Contains("/continue", output, StringComparison.Ordinal);
        Assert.Contains($"ru agent continue {runId}", output, StringComparison.Ordinal);
        Assert.Single(continuePosts);
        Assert.EndsWith($"/agent/v1/runs/{runId}/continue", continuePosts[0], StringComparison.Ordinal);
        Assert.Contains(eventUris, uri => uri.Contains("after_sequence=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatWithSession_runningRun_SuggestsAttach()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('e', 32);
        var posts = new List<string>();

        var running = JsonSerializer.Serialize(new
        {
            run_id = runId,
            session_id = "sess_123",
            workspace_id = "ws_test",
            status = "running",
            history_wait_kind = (string?)null,
            preset = "standard",
            policy_snapshot_version = 1,
            policy_snapshot_sha256 = new string('d', 64),
            version = 2L,
            last_sequence = 4L,
            created_at = "2026-09-30T00:00:00Z",
            updated_at = "2026-09-30T00:00:01Z",
            terminal_at = (string?)null,
            terminal_error_code = (string?)null,
        });

        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Get && uri.Contains("/agent/v1/sessions/sess_123/runs"))
            {
                var items = JsonSerializer.Serialize(new
                {
                    items = new[] { new { run_id = runId } },
                    next_cursor = (string?)null,
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(items, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.EndsWith(runId, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(running, Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post)
            {
                posts.Add(uri);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(session, new HttpClient(handler), outWriter, new StringWriter(), new StringReader("/exit\n"));

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        var output = outWriter.ToString();
        Assert.Contains($"ru agent attach {runId}", output, StringComparison.Ordinal);
        // Adoption points at a live run; it never follows, cancels or continues it.
        Assert.DoesNotContain(posts, uri => uri.EndsWith("/continue", StringComparison.Ordinal) || uri.EndsWith("/cancel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatWithSession_replays_recent_history()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var activateCalls = 0;
        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith("/sessions/sess_123/activate", StringComparison.Ordinal))
            {
                activateCalls++;
                var messages = Enumerable.Range(0, 25)
                    .Select(index => new { role = index % 2 == 0 ? "user" : "ai", content = $"history-marker-{index:00}" })
                    .ToArray();
                var payload = JsonSerializer.Serialize(new
                {
                    status = "ok",
                    session = new { id = "sess_123", title = "reopened", messages },
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            }

            if (req.Method == HttpMethod.Get && uri.Contains("/agent/v1/sessions/sess_123/runs"))
            {
                var items = JsonSerializer.Serialize(new { items = Array.Empty<object>(), next_cursor = (string?)null });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(items, Encoding.UTF8, "application/json") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(session, new HttpClient(handler), outWriter, new StringWriter(), new StringReader("/exit\n"));

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(1, activateCalls);
        var output = outWriter.ToString();
        Assert.Contains("replayed the last 20 messages from session sess_123", output, StringComparison.Ordinal);
        // The replay is the tail: marker 05 is the oldest of the last 20, marker 04 is out.
        Assert.Contains("history-marker-24", output, StringComparison.Ordinal);
        Assert.Contains("history-marker-05", output, StringComparison.Ordinal);
        Assert.DoesNotContain("history-marker-04", output, StringComparison.Ordinal);
        var replayedLines = output.Split('\n').Count(line => line.Contains("history-marker-", StringComparison.Ordinal));
        Assert.Equal(20, replayedLines);
    }

    [Fact]
    public async Task ChatWithSession_replay_failure_is_bounded_notice()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith("/sessions/sess_123/activate", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
            if (req.Method == HttpMethod.Get && uri.Contains("/agent/v1/sessions/sess_123/runs"))
            {
                var items = JsonSerializer.Serialize(new { items = Array.Empty<object>(), next_cursor = (string?)null });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(items, Encoding.UTF8, "application/json") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var commands = new AgentCommands(session, new HttpClient(handler), outWriter, new StringWriter(), new StringReader("/exit\n"));

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), CancellationToken.None);

        // Fail-soft: one bounded notice, then the REPL still starts and exits normally.
        Assert.Equal(0, exit);
        var output = outWriter.ToString();
        Assert.Contains("session_replay_unavailable", output, StringComparison.Ordinal);
        Assert.Equal(1, output.Split('\n').Count(line => line.Contains("session_replay_unavailable", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ChatAsync_SlashContinue_WithoutParkedRun_PrintsNotice()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        var outWriter = new StringWriter();
        var commands = new AgentCommands(session, new HttpClient(handler), outWriter, new StringWriter(), new StringReader("/continue\n"));

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("no parked run", outWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_SlashContinue_TerminalRunId_ReportsRunNotRunnable()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('c', 32);
        var continuePosts = 0;

        var terminal = JsonSerializer.Serialize(new
        {
            run_id = runId,
            session_id = "sess_123",
            workspace_id = "ws_test",
            status = "completed",
            history_wait_kind = (string?)null,
            preset = "standard",
            policy_snapshot_version = 1,
            policy_snapshot_sha256 = new string('d', 64),
            version = 1L,
            last_sequence = 4L,
            created_at = "2026-09-30T00:00:00Z",
            updated_at = "2026-09-30T00:00:03Z",
            terminal_at = "2026-09-30T00:00:03Z",
            terminal_error_code = (string?)null,
        });

        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/continue"))
            {
                continuePosts++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.EndsWith(runId, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(terminal, Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var errWriter = new StringWriter();
        var commands = new AgentCommands(session, new HttpClient(handler), new StringWriter(), errWriter, new StringReader($"/continue {runId}\n"));

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(0, continuePosts);
        Assert.Contains("run_not_runnable", errWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_SlashContinue_RunningRunId_ReportsRunNotParked()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('c', 32);
        var continuePosts = 0;

        var running = JsonSerializer.Serialize(new
        {
            run_id = runId,
            session_id = "sess_123",
            workspace_id = "ws_test",
            status = "running",
            history_wait_kind = (string?)null,
            preset = "standard",
            policy_snapshot_version = 1,
            policy_snapshot_sha256 = new string('d', 64),
            version = 1L,
            last_sequence = 4L,
            created_at = "2026-09-30T00:00:00Z",
            updated_at = "2026-09-30T00:00:03Z",
            terminal_at = (string?)null,
            terminal_error_code = (string?)null,
        });

        var handler = new FakeHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runId}/continue"))
            {
                continuePosts++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.EndsWith(runId, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(running, Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var errWriter = new StringWriter();
        var commands = new AgentCommands(session, new HttpClient(handler), new StringWriter(), errWriter, new StringReader($"/continue {runId}\n"));

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(0, continuePosts);
        Assert.Contains("run_not_parked", errWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_ContinueOtherRun_ThenCancel_PostsCancelForThatRun()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runA = "run_" + new string('a', 32);
        var runB = "run_" + new string('b', 32);
        var payloadRef = "payload_" + new string('e', 32);
        var cancelPosts = new List<string>();
        var continuePosts = new List<string>();

        string RunJson(string runId, string status, long lastSequence, string? terminalAt) => JsonSerializer.Serialize(new
        {
            run_id = runId,
            session_id = "sess_123",
            workspace_id = "ws_test",
            status,
            history_wait_kind = (string?)null,
            preset = "standard",
            policy_snapshot_version = 1,
            policy_snapshot_sha256 = new string('d', 64),
            version = 1L,
            last_sequence = lastSequence,
            created_at = "2026-09-30T00:00:00Z",
            updated_at = "2026-09-30T00:00:00Z",
            terminal_at = terminalAt,
            terminal_error_code = (string?)null,
        });

        string Durable(string runId, int seq, string eventType, string status, string summary, string? payloadRefValue = null) => JsonSerializer.Serialize(new
        {
            protocol_version = 1,
            event_id = $"evt_{runId[^1]}_{seq}",
            run_id = runId,
            session_id = "sess_123",
            sequence = seq,
            event_type = eventType,
            occurred_at = "2026-09-30T00:00:00Z",
            status,
            safe_summary = summary,
            payload_ref = payloadRefValue,
        });

        // Signalled once the /continue POST for B is in flight, so the test can cancel the
        // outer token while B's resumed stream is still being followed.
        var continuePosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new FakeHandler(async req =>
        {
            var uri = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && uri.Contains("/settings"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith("/agent/v1/policies"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { session_id = "sess_123", preset = "standard", version = 1 }), Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Post && uri.EndsWith($"/agent/v1/runs/{runB}/continue"))
            {
                continuePosts.Add(uri);
                continuePosted.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs") && !uri.EndsWith("/cancel"))
            {
                var created = JsonSerializer.Serialize(new
                {
                    run_id = runA,
                    session_id = "sess_123",
                    workspace_id = "ws_test",
                    status = "running",
                    preset = "standard",
                    created_at = DateTimeOffset.UtcNow.ToString("O"),
                    updated_at = DateTimeOffset.UtcNow.ToString("O"),
                });
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(created, Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Post && uri.EndsWith("/cancel"))
            {
                cancelPosts.Add(uri);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"cancelled\"}", Encoding.UTF8, "application/json") };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/payloads/"))
            {
                // A's parked turn carries a payload ref: the recovery fetch blocks and the
                // park-cancelled token surfaces the OCE-parked branch (no cancel for A).
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ParkedStream(Array.Empty<byte>()))
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                    },
                };
            }
            if (req.Method == HttpMethod.Get && uri.Contains("/events"))
            {
                if (uri.Contains($"/runs/{runA}/events"))
                {
                    var sse =
                        $"id: 1\ndata: {Durable(runA, 1, "model.step_completed", "completed", "step_done", payloadRef)}\n\n"
                        + $"id: 2\ndata: {Durable(runA, 2, "continue.requested", "waiting_user", "model_step_budget_exhausted")}\n\n";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new ParkedStream(Encoding.UTF8.GetBytes(sse)))
                        {
                            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") },
                        },
                    };
                }

                // B's resumed tail has no frames yet; hold the stream open.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ParkedStream(Array.Empty<byte>()))
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") },
                    },
                };
            }
            if (req.Method == HttpMethod.Get && uri.EndsWith(runA, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RunJson(runA, "waiting_user", 2, null), Encoding.UTF8, "application/json") };
            if (req.Method == HttpMethod.Get && uri.EndsWith(runB, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RunJson(runB, "waiting_user", 5, null), Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        using var cts = new CancellationTokenSource();
        var commands = new AgentCommands(session, new HttpClient(handler), outWriter, errWriter, new StringReader($"hello\n/continue {runB}\n"));

        var chat = commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), cts.Token);
        await continuePosted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        var exit = await chat;

        Assert.Equal(0, exit);
        Assert.Single(continuePosts);
        var cancelUri = Assert.Single(cancelPosts);
        Assert.EndsWith($"/agent/v1/runs/{runB}/cancel", cancelUri, StringComparison.Ordinal);
    }

    private sealed class BlockingStream : System.IO.Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    /// <summary>Serves its scripted SSE bytes once, then holds the stream open like a live tail.</summary>
    private sealed class ParkedStream : System.IO.Stream
    {
        private readonly MemoryStream _preface;

        public ParkedStream(byte[] data) => _preface = new MemoryStream(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _preface.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_preface.Position < _preface.Length)
            {
                return _preface.Read(buffer, offset, count);
            }

            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_preface.Position < _preface.Length)
            {
                return await _preface.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class BreakingStream : MemoryStream
    {
        public BreakingStream(byte[] data) : base(data, writable: false)
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= Length)
            {
                throw new IOException("Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.");
            }
            return base.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= Length)
            {
                throw new IOException("Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.");
            }
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ThrowOnWriteWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("output failed");
        public override void WriteLine() => throw new IOException("output failed");
    }

    private sealed class CountingReader : StringReader
    {
        private readonly Queue<string?> _lines;
        public CountingReader(string value) : base(value)
        {
            _lines = new Queue<string?>(value.Split('\n').Select(line => line.TrimEnd('\r')));
        }
        public int ReadCount { get; private set; }
        public override string? ReadLine()
        {
            ReadCount++;
            return _lines.Count == 0 ? null : _lines.Dequeue();
        }
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(_lines.Count == 0 ? null : _lines.Dequeue());
        }
    }

    private sealed class FakeHandler : HttpMessageHandler    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = req => Task.FromResult(func(req));
        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _func(request);
    }
}
