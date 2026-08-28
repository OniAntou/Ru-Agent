using System.Linq;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityUiSourceContractTests
{
    [Fact]
    public void Activity_feed_is_separate_from_messages_and_lives_inside_the_existing_scroll_host()
    {
        var xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        var codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        var model = File.ReadAllText(FindSource("Models", "AgentActivityItem.cs"));

        Assert.Contains("ObservableCollection<AgentActivityItem> AgentActivities", codeBehind, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentActivityFeed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsControl", xaml, StringComparison.Ordinal);
        Assert.Contains("ChatContentGrid", xaml, StringComparison.Ordinal);
        Assert.Contains("AgentActivityItem", model, StringComparison.Ordinal);
        Assert.DoesNotContain("ObservableCollection<ChatMessageItem> AgentActivities", codeBehind, StringComparison.Ordinal);

        var scrollStart = xaml.IndexOf("<ScrollViewer x:Name=\"ChatScrollHost\"", StringComparison.Ordinal);
        var scrollEnd = xaml.IndexOf("</ScrollViewer>", scrollStart, StringComparison.Ordinal);
        Assert.True(scrollStart >= 0 && scrollEnd > scrollStart, "ChatScrollHost must wrap the transcript.");
        var scrollBlock = xaml[scrollStart..scrollEnd];
        Assert.Equal(1, scrollBlock.Split("VerticalScrollBarVisibility=\"Auto\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", scrollBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollViewer VerticalScrollBarVisibility", scrollBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Feed_template_exposes_only_safe_state_and_metadata_visuals()
    {
        var xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        var feedStart = xaml.IndexOf("x:Name=\"AgentActivityFeed\"", StringComparison.Ordinal);
        Assert.True(feedStart >= 0, "AgentActivityFeed is missing.");
        var feedBlock = xaml[feedStart..];

        foreach (var state in new[] { "IsRunning", "IsCompleted", "IsFailed", "IsWaiting" })
        {
            Assert.Contains($"x:Bind {state}, Mode=OneWay", feedBlock, StringComparison.Ordinal);
        }
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", feedBlock, StringComparison.Ordinal);
        Assert.Contains("MetadataText", feedBlock, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "Command", "Arguments", "Diff", "Stdout", "Stderr", "Path" })
        {
            Assert.DoesNotContain(forbidden, feedBlock, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Agent_send_adds_a_temporary_user_message_and_activity_lifecycle_clears()
    {
        var codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        var sendStart = codeBehind.IndexOf("private async Task SendAgentTurnAsync", StringComparison.Ordinal);
        var sendEnd = codeBehind.IndexOf("private void BeginAgentReply", sendStart, StringComparison.Ordinal);
        Assert.True(sendStart >= 0 && sendEnd > sendStart, "SendAgentTurnAsync is missing.");
        var sendBlock = codeBehind[sendStart..sendEnd];
        Assert.Contains("ClearAgentActivities()", sendBlock, StringComparison.Ordinal);

        var replyEnd = codeBehind.IndexOf("private async Task CompleteActiveAgentTurnAsync", sendEnd, StringComparison.Ordinal);
        Assert.True(replyEnd > sendEnd, "BeginAgentReply must remain a separate helper.");
        var replyBlock = codeBehind[sendEnd..replyEnd];
        Assert.Contains("Messages.Add(new ChatMessageItem", replyBlock, StringComparison.Ordinal);

        foreach (var method in new[] { "private async Task<string?> ReloadSessionHistoryAsync", "private void ResetAgentState" })
        {
            var start = codeBehind.IndexOf(method, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Missing {method}.");
            var next = codeBehind.IndexOf("\n        private", start + method.Length, StringComparison.Ordinal);
            var body = next > start ? codeBehind[start..next] : codeBehind[start..];
            Assert.Contains("ClearAgentActivities()", body, StringComparison.Ordinal);
        }

        var clearStart = codeBehind.IndexOf("private void ClearAgentActivities", StringComparison.Ordinal);
        Assert.True(clearStart >= 0, "ClearAgentActivities is missing.");
        var clearEnd = codeBehind.IndexOf("\n        private", clearStart + 1, StringComparison.Ordinal);
        var clearBlock = clearEnd > clearStart ? codeBehind[clearStart..clearEnd] : codeBehind[clearStart..];
        Assert.Contains("AgentActivities.Clear()", clearBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Activity_item_contains_no_raw_payload_storage_or_reasoning_text()
    {
        var model = File.ReadAllText(FindSource("Models", "AgentActivityItem.cs"));

        Assert.DoesNotContain("TransientData", model, StringComparison.Ordinal);
        Assert.DoesNotContain("Command", model, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Arguments", model, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Diff", model, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stdout", model, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stderr", model, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reasoning", model, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("StartedText", model, StringComparison.Ordinal);
        Assert.Contains("CompletedText", model, StringComparison.Ordinal);
        Assert.Contains("FailedText", model, StringComparison.Ordinal);
        Assert.Contains("WaitingText", model, StringComparison.Ordinal);
    }

    private static string FindSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string sourceRoot = Path.Combine(directory.FullName, "gui-winui3");
            if (Directory.Exists(sourceRoot))
            {
                return Path.Combine(new[] { sourceRoot }.Concat(parts).ToArray());
            }
        }

        throw new DirectoryNotFoundException("gui-winui3 source root was not found.");
    }
}
