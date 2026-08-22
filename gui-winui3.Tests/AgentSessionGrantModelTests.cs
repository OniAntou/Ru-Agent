using System.Text.Json;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentSessionGrantModelTests
{
    [Fact]
    public void Accepted_frame_is_bounded_sorted_and_immutable()
    {
        using var document = JsonDocument.Parse("""
        {
          "type": "agent.session",
          "session_id": "session-1",
          "scopes": ["system.write", "system.read"]
        }
        """);

        Assert.True(AgentSessionGrantModel.TryParse(document.RootElement, out var grant));
        Assert.Equal("session-1", grant.SessionId);
        Assert.Equal(new[] { "system.read", "system.write" }, grant.Scopes);
        Assert.False(grant.Scopes is List<string>);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)grant.Scopes).Add("terminal.execute"));
    }

    [Theory]
    [MemberData(nameof(InvalidFrames))]
    public void Invalid_frame_is_rejected(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.False(AgentSessionGrantModel.TryParse(document.RootElement, out _));
    }

    [Fact]
    public void Constructor_rejects_duplicate_scopes_instead_of_silently_deduplicating()
    {
        Assert.Throws<ArgumentException>(() => new AgentSessionGrantModel(
            "session-1",
            new[] { "system.write", "system.write" }));
    }

    public static IEnumerable<object[]> InvalidFrames()
    {
        yield return new object[] { """{"type":"agent.session","scopes":["system.write"]}""" };
        yield return new object[] { """{"type":"agent.session","session_id":"   ","scopes":["system.write"]}""" };
        yield return new object[] { $$"""{"type":"agent.session","session_id":"{{new string('s', 129)}}","scopes":["system.write"]}""" };
        yield return new object[] { """{"type":"agent.session","session_id":"session-1","scopes":"system.write"}""" };
        yield return new object[] { """{"type":"agent.session","session_id":"session-1","scopes":["system.write","system.write"]}""" };
        yield return new object[] { """{"type":"agent.session","session_id":"session-1","scopes":[""]}""" };
        yield return new object[]
        {
            JsonSerializer.Serialize(new
            {
                type = "agent.session",
                session_id = "session-1",
                scopes = Enumerable.Range(0, 17).Select(index => $"scope.{index}").ToArray(),
            }),
        };
        yield return new object[] { $$"""{"type":"agent.session","session_id":"session-1","scopes":["{{new string('s', 65)}}"]}""" };
        yield return new object[] { """{"type":"agent.session","session_id":"session-1","scopes":[{}]}""" };
        yield return new object[] { """{"type":"agent.session","session_id":"session-1","scopes":["system.write"],"unexpected":true}""" };
        yield return new object[] { """{"type":"agent.activity","session_id":"session-1","scopes":["system.write"]}""" };
    }
}
