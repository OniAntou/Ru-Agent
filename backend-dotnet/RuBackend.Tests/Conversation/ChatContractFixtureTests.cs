using System.Text.Json;
using RuBackend.Conversation;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class ChatContractFixtureTests
{
    [Fact]
    public void Fixture_freezes_limits_validation_event_order_and_privacy()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FindFixturePath()));
        var root = fixture.RootElement;

        Assert.Equal(ChatRequest.MaxPromptCharacters, root.GetProperty("maxPromptCharacters").GetInt32());
        Assert.Equal(ChatRequest.MaxMessageBytes, root.GetProperty("maxMessageBytes").GetInt32());
        Assert.Equal(
            ["blank prompt", "prompt over limit"],
            root.GetProperty("validationCases").EnumerateArray()
                .Select(item => item.GetProperty("name").GetString()));
        Assert.All(
            root.GetProperty("validationCases").EnumerateArray(),
            item => Assert.False(item.GetProperty("accepted").GetBoolean()));
        Assert.Equal(
            ["session", "reset", "chunk", "done"],
            root.GetProperty("eventCases")[0].GetProperty("eventTypes").EnumerateArray()
                .Select(item => item.GetString()));
        Assert.Equal(
            ["error"],
            root.GetProperty("eventCases")[2].GetProperty("eventTypes").EnumerateArray()
                .Select(item => item.GetString()));

        var request = new ChatRequest("   ", null);
        Assert.Equal("   ", request.Prompt);
        Assert.Null(request.SessionId);
        var privacy = root.GetProperty("privacyCase");
        Assert.NotEqual(
            privacy.GetProperty("rawReply").GetString(),
            privacy.GetProperty("visibleReply").GetString());
    }

    [Fact]
    public void Public_event_serialization_is_camel_case_and_omits_private_nulls()
    {
        var json = JsonSerializer.Serialize(new ChatEvent(
            "done",
            "Em nghe đây.",
            "session-20260811-120000-abcd1234"));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("done", root.GetProperty("type").GetString());
        Assert.Equal("Em nghe đây.", root.GetProperty("content").GetString());
        Assert.Equal(
            "session-20260811-120000-abcd1234",
            root.GetProperty("session_id").GetString());
        Assert.False(root.TryGetProperty("error_code", out _));
        Assert.Equal(3, root.EnumerateObject().Count());
    }

    private static string FindFixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "tests",
                "fixtures",
                "chat-orchestration.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate tests/fixtures/chat-orchestration.json.");
    }
}
