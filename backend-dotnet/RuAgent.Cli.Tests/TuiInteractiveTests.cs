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
    public void AnsiTheme_ProducesColor_WhenEnabled()
    {
        AnsiTheme.IsEnabled = true;
        try
        {
            var colored = AnsiTheme.Green("hello");
            Assert.Contains("\u001b[32mhello\u001b[0m", colored, StringComparison.Ordinal);

            var diffAdded = AnsiTheme.FormatDiffLine("+ public void Run()");
            Assert.Contains("\u001b[32m", diffAdded, StringComparison.Ordinal);

            var diffRemoved = AnsiTheme.FormatDiffLine("- public void Stop()");
            Assert.Contains("\u001b[31m", diffRemoved, StringComparison.Ordinal);

            var diffHeader = AnsiTheme.FormatDiffLine("@@ -1,5 +1,6 @@");
            Assert.Contains("\u001b[36m", diffHeader, StringComparison.Ordinal);
        }
        finally
        {
            AnsiTheme.IsEnabled = false;
        }
    }

    [Fact]
    public void AnsiTheme_DisablesColor_WhenDisabled()
    {
        AnsiTheme.IsEnabled = false;
        var text = AnsiTheme.Green("hello");
        Assert.Equal("hello", text);

        var diff = AnsiTheme.FormatDiffLine("+ added line");
        Assert.Equal("+ added line", diff);
    }

    [Fact]
    public void TuiEventRenderer_RendersTurnStarted_AndToolExecution()
    {
        AnsiTheme.IsEnabled = false;
        using var sw = new StringWriter();

        var envelope = new HarnessSafeEventEnvelope(
            ProtocolVersion: 1,
            EventId: "evt_1",
            RunId: "run_1",
            SessionId: "sess_1",
            Sequence: 1,
            EventType: "model.turn_started",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
            Status: "running",
            SafeSummary: "Thinking about plan");

        TuiEventRenderer.RenderEnvelope(envelope, sw);
        var output = sw.ToString();
        Assert.Contains("◆", output, StringComparison.Ordinal);
        Assert.Contains("Turn #1", output, StringComparison.Ordinal);

        using var sw2 = new StringWriter();
        var toolEnvelope = new HarnessSafeEventEnvelope(
            ProtocolVersion: 1,
            EventId: "evt_2",
            RunId: "run_1",
            SessionId: "sess_1",
            Sequence: 2,
            EventType: "tool.execution_requested",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
            Status: "running",
            SafeSummary: "read op=read path: src/main.rs");

        TuiEventRenderer.RenderEnvelope(toolEnvelope, sw2);
        var toolOutput = sw2.ToString();
        Assert.Contains("read src/main.rs", toolOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TuiEventRenderer_RendersRunCompleted_SuccessAndFailure()
    {
        AnsiTheme.IsEnabled = false;
        using var sw = new StringWriter();

        var successEnv = new HarnessSafeEventEnvelope(
            ProtocolVersion: 1,
            EventId: "evt_5",
            RunId: "run_1",
            SessionId: "sess_1",
            Sequence: 5,
            EventType: "run.completed",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
            Status: "succeeded",
            SafeSummary: "Task completed successfully");

        TuiEventRenderer.RenderEnvelope(successEnv, sw);
        Assert.Contains("Turn completed in", sw.ToString(), StringComparison.Ordinal);

        using var sw2 = new StringWriter();
        var failEnv = new HarnessSafeEventEnvelope(
            ProtocolVersion: 1,
            EventId: "evt_6",
            RunId: "run_1",
            SessionId: "sess_1",
            Sequence: 6,
            EventType: "run.completed",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
            Status: "failed",
            SafeSummary: "Timeout exceeded");

        TuiEventRenderer.RenderEnvelope(failEnv, sw2);
        Assert.Contains("Run #6 FAILED", sw2.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TuiEventRenderer_FiltersInternalUsageRecordedEvents()
    {
        AnsiTheme.IsEnabled = false;
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

        var res3 = AgentCliParser.Parse(["repl", "--preset", "full"]);
        Assert.True(res3.Success);
        var cmd3 = Assert.IsType<ChatCommand>(res3.Command);
        Assert.Equal("full_access", cmd3.Preset);
    }

    [Fact]
    public void Parser_ParsesSessionSubcommands()
    {
        // session list
        var resList = AgentCliParser.Parse(["session", "list", "--json"]);
        Assert.True(resList.Success);
        var cmdList = Assert.IsType<SessionListCommand>(resList.Command);
        Assert.True(cmdList.Json);

        // session fork
        var resFork = AgentCliParser.Parse(["agent", "session", "fork", "sess_parent_123", "--index", "3", "--title", "Feature Branch"]);
        Assert.True(resFork.Success);
        var cmdFork = Assert.IsType<SessionForkCommand>(resFork.Command);
        Assert.Equal("sess_parent_123", cmdFork.SessionId);
        Assert.Equal(3, cmdFork.UpToMessageIndex);
        Assert.Equal("Feature Branch", cmdFork.NewTitle);

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
        var res1 = AgentCliParser.Parse(["session", "fork"]);
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
        string[] cmds = ["/help", "/model", "/backend", "/fork", "/rewind", "/tree", "/stats", "/tokens", "/export", "/status", "/history", "/clear", "/exit", "/quit"];
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
        var stripped = InteractiveLineReader.StripAnsi(mid);

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
        var stripped = InteractiveLineReader.StripAnsi(bottom);

        Assert.Equal(width, stripped.Length);
        Assert.StartsWith("╰", stripped, StringComparison.Ordinal);
        Assert.EndsWith("╯", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void TuiEventRenderer_RendersToolIcons_And_Diffs()
    {
        AnsiTheme.IsEnabled = false;
        using var sw = new StringWriter();

        var editEnv = new HarnessSafeEventEnvelope(
            ProtocolVersion: 1,
            EventId: "evt_edit",
            RunId: "run_1",
            SessionId: "sess_1",
            Sequence: 10,
            EventType: "tool.execution_requested",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
            Status: "running",
            SafeSummary: "edit path: src/app.cs");

        TuiEventRenderer.RenderEnvelope(editEnv, sw);
        var outReq = sw.ToString();
        Assert.Contains("edit src/app.cs", outReq);

        using var sw2 = new StringWriter();
        var doneEnv = new HarnessSafeEventEnvelope(
            ProtocolVersion: 1,
            EventId: "evt_done",
            RunId: "run_1",
            SessionId: "sess_1",
            Sequence: 11,
            EventType: "tool.executed",
            OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
            Status: "succeeded",
            SafeSummary: "edit completed\n--- a/file.cs\n+++ b/file.cs\n@@ -1,2 +1,2 @@\n-old line\n+new line");

        TuiEventRenderer.RenderEnvelope(doneEnv, sw2);
        var outDone = sw2.ToString();
        Assert.Contains("◆ edit", outDone);
        Assert.Contains("+new line", outDone);
        Assert.Contains("-old line", outDone);
    }

    [Fact]
    public async Task ChatAsync_HandlesWaitingWorkspaceTrust_PromptsAndResumes()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('a', 32);
        var trustReqId = "tr_" + new string('b', 32);
        var displayHash = new string('c', 64);
        bool trustCalled = false;

        var handler = new FakeHandler(req =>
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
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs") && !uri.Contains("/trust"))
            {
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

        var inReader = new StringReader("hello\n1\n/exit\n");
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var httpClient = new HttpClient(handler);

        var commands = new AgentCommands(session, httpClient, outWriter, errWriter, inReader);
        var cmd = new ChatCommand("C:\\untrusted\\workspace", "sess_123", "standard", false);

        var exit = await commands.ChatAsync(cmd, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(trustCalled);
        var outStr = outWriter.ToString();
        Assert.Contains("Trust this workspace?", outStr);
        Assert.Contains("Workspace trusted", outStr);
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
    public void InteractiveLineReader_BuildSubmitEcho_IsFilledBar()
    {
        bool prev = AnsiTheme.IsEnabled;
        AnsiTheme.IsEnabled = true;
        try
        {
            var echo = InteractiveLineReader.BuildSubmitEcho("Ê");
            var rows = echo.Split('\n');

            Assert.Equal(3, rows.Length);
            Assert.Contains("Ê", echo);
            Assert.Contains(">", echo);
            Assert.Contains("\x1b[100m", echo); // dark-gray fill like the sample bar
            Assert.DoesNotContain("│", echo);
            Assert.DoesNotContain("╭", echo);
            foreach (var row in rows)
            {
                Assert.Equal(80, InteractiveLineReader.StripAnsi(row).Length);
            }
        }
        finally
        {
            AnsiTheme.IsEnabled = prev;
        }
    }

    [Fact]
    public void InteractiveLineReader_BuildSubmitEcho_FlattensMultiline()
    {
        bool prev = AnsiTheme.IsEnabled;
        AnsiTheme.IsEnabled = true;
        try
        {
            var echo = InteractiveLineReader.BuildSubmitEcho("a\nb");
            var rows = echo.Split('\n');

            Assert.Equal(3, rows.Length);
            Assert.Contains("a b", rows[1]);
            Assert.DoesNotContain("a\nb", echo);
        }
        finally
        {
            AnsiTheme.IsEnabled = prev;
        }
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
    public async Task ChatAsync_NeverAutoApprovesTrust_InJsonMode()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        var session = PublicHostSession.FromDescriptor(desc);

        var runId = "run_" + new string('a', 32);
        var trustReqId = "tr_" + new string('b', 32);
        var displayHash = new string('c', 64);
        bool trustCalled = false;

        var handler = new FakeHandler(req =>
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
            if (req.Method == HttpMethod.Post && uri.Contains("/agent/v1/runs") && !uri.Contains("/trust"))
            {
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

        Assert.Equal(0, exit);
        Assert.False(trustCalled);
        var outStr = outWriter.ToString();
        Assert.Contains("waiting_workspace_trust", outStr);
        Assert.DoesNotContain("Trust this workspace?", outStr);
    }

    [Fact]
    public void InteractiveLineReader_BuildBottom_NeverSplitsAnsi_OnLongStyledLegend()
    {
        int width = 40;
        string styledLegend = "\x1b[36m" + new string('L', 100) + "\x1b[0m";
        var bottom = InteractiveLineReader.BuildBottom(width, styledLegend, false);
        var stripped = InteractiveLineReader.StripAnsi(bottom);

        Assert.Equal(width, stripped.Length);
        int escCount = System.Text.RegularExpressions.Regex.Matches(bottom, "\x1b").Count;
        int seqCount = System.Text.RegularExpressions.Regex.Matches(bottom, "\x1b\\[[0-9;]*[a-zA-Z]").Count;
        Assert.Equal(escCount, seqCount);
    }

    private sealed class FakeHandler : HttpMessageHandler    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_func(request));
    }
}
