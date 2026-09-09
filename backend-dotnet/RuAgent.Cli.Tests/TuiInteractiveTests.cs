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
        Assert.Contains("Turn #1: Model Thinking...", output, StringComparison.Ordinal);

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
        Assert.Contains("Tool Request #2", toolOutput, StringComparison.Ordinal);
        Assert.Contains("read", toolOutput, StringComparison.Ordinal);
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
        Assert.Contains("Run #5 Succeeded", sw.ToString(), StringComparison.Ordinal);

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
        Assert.Contains("✏️", outReq);
        Assert.Contains("Tool Request #10", outReq);

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
        Assert.Contains("✓ ✏️ [Tool Done #11]", outDone);
        Assert.Contains("+new line", outDone);
        Assert.Contains("-old line", outDone);
    }
}
