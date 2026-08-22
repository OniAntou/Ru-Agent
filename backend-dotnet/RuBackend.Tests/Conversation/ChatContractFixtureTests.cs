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

    [Fact]
    public void Agent_session_serializes_only_the_bounded_actual_scopes()
    {
        var json = JsonSerializer.Serialize(new ChatEvent(
            "agent.session",
            SessionId: "session-1",
            Scopes: ["system.write"]));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("agent.session", root.GetProperty("type").GetString());
        Assert.Equal("session-1", root.GetProperty("session_id").GetString());
        Assert.Equal(
            ["system.write"],
            root.GetProperty("scopes").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(3, root.EnumerateObject().Count());
    }

    [Theory]
    [MemberData(nameof(InvalidScopes))]
    public void Agent_session_rejects_invalid_scope_lists(IReadOnlyList<string> scopes)
    {
        Assert.Throws<ArgumentException>(() => new ChatEvent(
            "agent.session",
            SessionId: "session-1",
            Scopes: scopes));
    }

    public static IEnumerable<object[]> InvalidScopes()
    {
        yield return new object[] { Enumerable.Repeat("scope", 17).ToArray() };
        yield return new object[] { new[] { string.Empty } };
        yield return new object[] { new[] { new string('s', 65) } };
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
