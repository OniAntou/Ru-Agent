using System.Text;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class RestoreContentTests
{
    [Fact]
    public void Empty_base64_restores_an_originally_empty_file()
    {
        var ok = AgentV1Shared.TryReadRestoreContent(Encoding.UTF8.GetBytes("""{"content_base64":""}"""), out var content);

        Assert.True(ok);
        Assert.Empty(content);
    }

    [Theory]
    [InlineData("""{"content_base64":null}""")]
    [InlineData("""{}""")]
    [InlineData("""{"content_base64":"!!not-base64"}""")]
    public void Missing_null_or_invalid_content_is_unavailable(string json)
    {
        Assert.False(AgentV1Shared.TryReadRestoreContent(Encoding.UTF8.GetBytes(json), out _));
    }

    [Fact]
    public void Non_empty_content_round_trips()
    {
        var payload = $$"""{"content_base64":"{{Convert.ToBase64String("abc"u8)}}"}""";

        Assert.True(AgentV1Shared.TryReadRestoreContent(Encoding.UTF8.GetBytes(payload), out var content));
        Assert.Equal("abc"u8.ToArray(), content);
    }
}
