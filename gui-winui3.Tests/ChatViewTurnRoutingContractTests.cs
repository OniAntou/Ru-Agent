using System.Text.Json;
using RuAgent.Client;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Text turns travel one way only: an authenticated Agent run whose output arrives
/// as typed SSE events. These assertions hold that contract at the level it is
/// declared — the route table and the controller's surface — instead of looking for
/// identifiers in the code-behind.
/// </summary>
public sealed class ChatViewTurnRoutingContractTests
{
    [Fact]
    public void The_public_route_table_exposes_no_second_chat_transport()
    {
        var json = GuiSource.Text("..", "backend-dotnet", "public-route-contract.json");
        using var document = JsonDocument.Parse(json);

        var paths = document.RootElement.EnumerateArray()
            .Select(entry => entry.GetProperty("path").GetString())
            .Where(path => path is not null)
            .ToList();

        // The surface is the Agent API plus the host/business routes; no chat route.
        Assert.DoesNotContain(paths, path => path!.Contains("chat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("/agent/v1/runs", paths);
        Assert.Contains("/agent/v1/runs/{runId}/events", paths);
    }

    [Fact]
    public void The_run_controller_owns_the_whole_turn_lifecycle()
    {
        var type = typeof(AgentRunController);

        // Start, observe, resume, stop: all on the one controller the view binds to.
        Assert.NotNull(type.GetMethod(nameof(AgentRunController.CreateAndRunAsync)));
        Assert.NotNull(type.GetMethod(nameof(AgentRunController.RefreshAsync)));
        Assert.NotNull(type.GetMethod(nameof(AgentRunController.CancelAsync)));
        Assert.NotNull(type.GetMethod(nameof(AgentRunController.ApproveAsync)));
        Assert.NotNull(type.GetMethod(nameof(AgentRunController.DenyAsync)));

        // Output arrives as typed events, not as raw frames.
        Assert.NotNull(type.GetEvent(nameof(AgentRunController.AssistantDeltaReceived)));
        Assert.NotNull(type.GetEvent(nameof(AgentRunController.ToolOutputReceived)));
        Assert.NotNull(type.GetEvent(nameof(AgentRunController.ActivityReceived)));
        Assert.NotNull(type.GetEvent(nameof(AgentRunController.SummaryReceived)));
    }

    [Fact]
    public void Delta_and_tool_models_stay_typed_instead_of_carrying_raw_frames()
    {
        // The desktop never sees envelope JSON: the client parses frames into these
        // records, so a raw-frame escape hatch would have to be added here first.
        Assert.NotNull(typeof(AgentAssistantDeltaModel).GetProperty(nameof(AgentAssistantDeltaModel.Text)));
        Assert.NotNull(typeof(AgentAssistantDeltaModel).GetProperty(nameof(AgentAssistantDeltaModel.ModelStepId)));
        Assert.NotNull(typeof(AgentToolOutputModel).GetProperty(nameof(AgentToolOutputModel.Text)));
        Assert.NotNull(typeof(AgentActivityModel).GetProperty(nameof(AgentActivityModel.ActivityId)));

        // None of them exposes the raw SSE payload.
        foreach (var type in new[]
                 {
                     typeof(AgentAssistantDeltaModel),
                     typeof(AgentToolOutputModel),
                     typeof(AgentActivityModel),
                 })
        {
            Assert.DoesNotContain(
                type.GetProperties(),
                property => property.Name.Contains("Raw", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("Frame", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("Json", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_obsolete_chat_transport_types_are_gone_from_the_shipped_client()
    {
        // A WebSocket chat transport and its presenter were removed with the Chat
        // compatibility path; nothing may reintroduce them under the same names.
        var assembly = typeof(AgentRunController).Assembly;
        foreach (var name in new[] { "ChatTurnPresenter", "ChatWebSocketClient", "ChatTurnQueue" })
        {
            Assert.Null(assembly.GetType("RuApp.Services." + name));
            Assert.Null(assembly.GetType("RuApp.Views." + name));
        }

        Assert.Null(typeof(AgentRunController).GetMethod("OnWsFrameReceived", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
    }
}
