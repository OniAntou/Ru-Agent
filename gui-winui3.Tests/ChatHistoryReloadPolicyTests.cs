using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatHistoryReloadPolicyTests
{
    [Fact]
    public void Missing_latest_assistant_message_preserves_streamed_reply()
    {
        var history = new List<(string Role, string Content)>
        {
            ("user", "hello"),
        };

        Assert.True(ChatHistoryReloadPolicy.ShouldPreserveStreamedReply(
            history,
            "hello from Agent"));
    }

    [Fact]
    public void Persisted_latest_assistant_message_allows_history_reload()
    {
        var history = new List<(string Role, string Content)>
        {
            ("user", "hello"),
            ("assistant", "hello from Agent"),
        };

        Assert.False(ChatHistoryReloadPolicy.ShouldPreserveStreamedReply(
            history,
            "hello from Agent"));
    }

    [Fact]
    public void Matching_older_assistant_message_does_not_hide_new_streamed_reply()
    {
        var history = new List<(string Role, string Content)>
        {
            ("user", "first"),
            ("assistant", "hello from Agent"),
            ("user", "again"),
        };

        Assert.True(ChatHistoryReloadPolicy.ShouldPreserveStreamedReply(
            history,
            "hello from Agent"));
    }
}
