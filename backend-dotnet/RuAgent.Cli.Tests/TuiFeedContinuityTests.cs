using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiFeedContinuityTests
{
    [Fact]
    public void LateToolDetailsUpdateTheExistingRowWithoutOverwritingNarration()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var renderer = AgentCommands.CreateSessionRenderer(new TgTranscriptWriter(app, window.Transcript));
        var activity = new AgentActivityModel("read_a", AgentActivityKind.Explore, AgentActivityState.Started,
            "sample.txt", null, null, null, null, null, null, null, "ok", "Read", null, null);
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            renderer.BeginTurn();
            renderer.Transcript.ApplyActivity(activity);
            renderer.RenderEnvelope(Event("tool.execution_completed", 1));
            Assert.Contains(window.Transcript.Lines, row => row.Style == TranscriptStyle.Tool && row.Text.StartsWith("· Read"));
            renderer.Transcript.AppendAssistant("Here is the result.");
            renderer.Flush();
            renderer.Transcript.ApplyActivity(activity with { State = AgentActivityState.Completed, DurationMs = 120 });
            Assert.Single(window.Transcript.Lines, row => row.Style == TranscriptStyle.Tool && row.Text.StartsWith("✓ Read"));
            Assert.Contains(window.Transcript.Lines, row => row.Text == "Here is the result.");
            Assert.Equal(1, renderer.Transcript.ToolsSettled);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Theory]
    [InlineData("running", AgentActivityState.Completed, "✓")]
    [InlineData("completed", AgentActivityState.Failed, "✗")]
    [InlineData("failed", AgentActivityState.Completed, "✓")]
    [InlineData("running", AgentActivityState.Started, "·")]
    [InlineData("failed", AgentActivityState.Started, "·")]
    public void RunStatusDoesNotMasqueradeAsTheToolOutcome(string runStatus, AgentActivityState toolState, string glyph)
    {
        using var output = new StringWriter();
        var surface = new TuiSurface(output) { SuppressRepaints = true };
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { FullScreenCards = true };
        transcript.ApplyActivity(new AgentActivityModel("read_a", AgentActivityKind.Explore, toolState,
            "sample.txt", null, null, null, null, null, null, 100, "ok", "Read", null, null));
        transcript.ApplyDurable(Event("tool.execution_completed", 1, runStatus));
        Assert.StartsWith(glyph + " Read", TuiText.Strip(output.ToString()));
    }

    [Theory]
    [InlineData("model.usage_recorded")]
    [InlineData("model.reasoning_recorded")]
    [InlineData("prompt_receipt")]
    [InlineData("tool.proposed")]
    [InlineData("run.state_changed")]
    public void MetadataBetweenDeltasDoesNotSplitTheSentence(string eventType)
    {
        using var output = new StringWriter();
        var surface = new TuiSurface(output) { SuppressRepaints = true };
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface));
        transcript.BeginTurn();
        transcript.AppendAssistant("Em đang kiểm");
        transcript.ApplyDurable(Event(eventType, 1));
        transcript.AppendAssistant(" tra **kết quả** cho anh.");
        transcript.Flush();
        Assert.Contains("Em đang kiểm tra kết quả cho anh.", TuiText.Strip(output.ToString()));
    }

    [Fact]
    public void ScreenSentenceSurvivesMetadataAndReasoningUpdates()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var renderer = AgentCommands.CreateSessionRenderer(new TgTranscriptWriter(app, window.Transcript));
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            renderer.BeginTurn();
            renderer.Transcript.AppendAssistant("Cấu trúc dự");
            renderer.RenderEnvelope(Event("model.reasoning_recorded", 1));
            renderer.Transcript.AppendAssistant(" án đã đầy đủ.");
            renderer.Flush();
            Assert.Contains(window.Transcript.Lines, row => row.Text == "Cấu trúc dự án đã đầy đủ.");
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    private static HarnessSafeEventEnvelope Event(string type, long sequence, string status = "running") =>
        new(1, $"evt_{sequence}", "run_test", "session_test", sequence, type,
            DateTimeOffset.UtcNow.ToString("O"), status, "ok");

}
