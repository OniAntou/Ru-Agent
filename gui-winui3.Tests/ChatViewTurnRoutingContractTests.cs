using Xunit;

namespace RuApp.Tests;

public sealed class ChatViewTurnRoutingContractTests
{
    [Fact]
    public void ChatView_does_not_route_stream_frames_to_the_last_streaming_assistant_bubble()
    {
        var source = File.ReadAllText(FindChatViewPath());

        Assert.DoesNotContain(
            "LastOrDefault(m => m.Role == \"assistant\" && m.IsStreaming)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ChatTurnPresenter", source, StringComparison.Ordinal);
        Assert.Contains("OnWsFrameReceived", source, StringComparison.Ordinal);
        Assert.Contains("MarkStreamingChatMessagesAsError", source, StringComparison.Ordinal);
        Assert.Contains(
            "item.Role == \"assistant\" &&\n                string.Equals(item.TurnId, update.Turn.TurnId",
            source,
            StringComparison.Ordinal);
        Assert.Contains("_chatTurnQueue.Remove(turn.CorrelationId);", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Content = $\"**LỖI GỬI:** {exception.Message}\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Content = $\"**LỖI MỞ SESSION:** {ex.Message}\"",
            source,
            StringComparison.Ordinal);
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
