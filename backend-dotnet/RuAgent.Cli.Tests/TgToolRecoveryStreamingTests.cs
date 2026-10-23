using System.Text.Json;
using RuAgent.Cli.TerminalGui;
using RuAgent.Client;
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
