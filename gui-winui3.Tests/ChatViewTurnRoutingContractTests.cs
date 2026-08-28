using Xunit;

namespace RuApp.Tests;

public sealed class ChatViewTurnRoutingContractTests
{
    [Fact]
    public void ChatView_routes_typed_deltas_through_the_active_agent_run()
    {
        var source = File.ReadAllText(FindChatViewPath());

        Assert.Contains("SendAgentTurnAsync", source, StringComparison.Ordinal);
        Assert.Contains("OnAgentAssistantDeltaReceived", source, StringComparison.Ordinal);
        Assert.Contains("_activeAgentRunId", source, StringComparison.Ordinal);
        Assert.Contains("_agentController.CreateAndRunAsync", source, StringComparison.Ordinal);
        Assert.Contains("CompleteActiveAgentTurnAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ChatTurnPresenter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ChatWebSocketClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OnWsFrameReceived", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_chatTurnQueue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/chat", source, StringComparison.Ordinal);
    }

    private static string FindChatViewPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "gui-winui3", "Views", "ChatView.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate gui-winui3/Views/ChatView.xaml.cs.");
    }
}
