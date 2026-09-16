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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_was_rendered_so_there_is_nothing_to_preserve(string? streamedReply)
    {
        // The caller used to re-check this itself before using the answer.
        Assert.False(ChatHistoryReloadPolicy.ShouldPreserveStreamedReply(
            [("user", "hello")],
            streamedReply));
    }

    [Fact]
    public void A_tool_envelope_never_stays_on_screen_as_a_reply()
    {
        // The envelope is persisted for the model but is not an answer to the user;
        // keeping it would render machine text as the assistant's reply.
        string envelope = "<tool_call>{\"tool\":\"read\"}</tool_call>";

        Assert.False(ChatHistoryReloadPolicy.ShouldPreserveStreamedReply(
            [("user", "read the readme")],
            envelope));
    }

    [Fact]
    public void An_empty_snapshot_is_delivery_lag_and_keeps_the_reply()
    {
        // History arrived before the outbox committed the turn; the reply the user
        // watched stream must not be replaced by nothing.
        Assert.True(ChatHistoryReloadPolicy.ShouldPreserveStreamedReply([], "hello from Agent"));
    }
}
