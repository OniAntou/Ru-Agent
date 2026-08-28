using System.Text.Json;
using RuBackend.Conversation;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class ChatCorrelationTests
{
    [Fact]
    public void Generated_id_is_a_valid_chat_correlation()
    {
        var id = ChatCorrelation.Generate();

        Assert.StartsWith("c_", id, StringComparison.Ordinal);
        Assert.True(ChatCorrelation.IsValid(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("c_invalid space")]
    [InlineData("c_invalid.dot")]
    public void Invalid_ids_are_rejected(string? id)
    {
        Assert.False(ChatCorrelation.IsValid(id));
        Assert.Null(ChatCorrelation.Normalize(id));
    }

    [Fact]
    public void Normalize_trims_valid_ids_without_changing_the_value()
    {
        Assert.Equal("c_12345678", ChatCorrelation.Normalize("  c_12345678  "));
    }

    [Fact]
    public void Chat_event_serializes_correlation_id_when_present()
    {
        var json = JsonSerializer.Serialize(new ChatEvent(
            "done",
            "Em nghe đây.",
            "session-1",
            CorrelationId: "c_12345678"));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("c_12345678", root.GetProperty("correlation_id").GetString());
        Assert.Equal(4, root.EnumerateObject().Count());
    }
}
