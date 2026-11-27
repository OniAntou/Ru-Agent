using System.Text.Json;
using RuAgent.Cli.TerminalGui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgToolRecoveryStreamingTests
{
    private const string Earlier = "step_11111111111111111111111111111111";
    private const string Rejected = "step_22222222222222222222222222222222";
    private const string Replacement = "step_33333333333333333333333333333333";
    [Fact]
    public void Recovery_reset_retracts_a_finished_answer_before_a_different_step_replies()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var renderer = AgentCommands.CreateSessionRenderer(new TgTranscriptWriter(app, window.Transcript));
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            renderer.BeginTurn();
            renderer.RenderFrame(Delta(Earlier, 1, "Earlier accepted text.\n"));
            renderer.Flush();
            renderer.RenderFrame(Delta(Rejected, 2, "Rejected error summary.\n"));
            renderer.Flush();
            Assert.Contains(window.Transcript.Lines, row => row.Text == "Rejected error summary.");

            renderer.RenderFrame(Delta(Rejected, 3, "", reset: true));
            renderer.RenderFrame(Delta(Replacement, 4, "Final error report.\n"));
            renderer.Flush();

            Assert.DoesNotContain(window.Transcript.Lines, row => row.Text.Contains("Rejected"));
            Assert.Single(window.Transcript.Lines, row => row.Text == "Final error report.");
            Assert.Contains(window.Transcript.Lines, row => row.Text == "Earlier accepted text.");
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Theory]
    [InlineData("Kiểm tra bản 4200 trên máy mới.", false)]
    [InlineData("Kiểm tra bản ", false)]
    [InlineData("Kiểm tra bản 420 trên máy mới.", false)]
    [InlineData("Kiểm tra bản 4200 trên máy mới.", true)]
    public void Narration_is_shown_once_when_the_same_step_also_streams(string streamed, bool activityFirst)
    {
        using IApplication app = Application.Create();
        using var view = new TgTranscriptView { Frame = new System.Drawing.Rectangle(0, 0, 90, 15) };
        using var writer = new TgTranscriptWriter(app, view, dispatcher: action => action());
        var renderer = AgentCommands.CreateSessionRenderer(writer);
        const string narration = "Kiểm tra bản 4200 trên máy mới.";
        renderer.BeginTurn();

        if (activityFirst)
        {
            renderer.RenderFrame(Narration(Earlier, 1, narration));
            renderer.RenderFrame(Delta(Earlier, 2, streamed));
        }
        else
        {
            renderer.RenderFrame(Delta(Earlier, 1, streamed));
            renderer.RenderFrame(Narration(Earlier, 2, narration));
        }
        renderer.RenderFrame(Narration(Earlier, 3, narration));
        renderer.Flush();

        var lines = view.Lines.Select(row => row.Text).ToArray();
        Assert.Single(lines, text => text.Contains("Kiểm tra bản", StringComparison.Ordinal));
        Assert.Contains(lines, text => text.EndsWith(narration, StringComparison.Ordinal));

        // Identical text from another model step is still a separate narration.
        renderer.RenderFrame(Narration(Replacement, 4, narration));
        renderer.Flush();
        Assert.Equal(2, view.Lines.Count(row => row.Text.EndsWith(narration, StringComparison.Ordinal)));
    }

    private static SseFrame Narration(string step, long sequence, string text)
    {
        var activity = new AgentActivityWire(AgentActivityIdentity.NarrationForStep(step),
            AgentActivityKind.Narration, AgentActivityState.Completed, Detail: text);
        return new SseFrame(null, "transient", JsonSerializer.Serialize(new
        {
            kind = "agent.activity", data = AgentActivityWireCodec.Serialize(activity),
        }), true, sequence);
    }

    private static SseFrame Delta(string step, long sequence, string text, bool reset = false)
    {
        var fields = new Dictionary<string, object?>
        {
            ["text"] = text, ["model_step_id"] = step, ["execution_id"] = null,
        };
        if (reset) fields["reset"] = true;
        var frame = new SseFrame(null, "transient", JsonSerializer.Serialize(new
        {
            kind = "assistant.delta", data = JsonSerializer.Serialize(fields),
        }), true, sequence);
        Assert.True(AgentAssistantDeltaModel.TryParse(frame, out _));
        return frame;
    }
}
