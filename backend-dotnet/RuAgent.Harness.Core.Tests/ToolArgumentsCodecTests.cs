using System.Text.Json;
using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ToolArgumentsCodecTests
{
    [Fact]
    public void Serialize_and_deserialize_preserve_the_durable_tool_call_envelope()
    {
        using var argumentsDocument = JsonDocument.Parse("{\"path\":\"src/file.cs\",\"nested\":[1,true]}");
        var serialized = ToolArgumentsCodec.Serialize(
            "call_1",
            "read",
            "1.0.0",
            argumentsDocument.RootElement);

        var envelope = ToolArgumentsCodec.Deserialize(serialized);

        Assert.Equal("call_1", envelope.ProviderCallId);
        Assert.Equal("read", envelope.ToolId);
        Assert.Equal("1.0.0", envelope.ToolVersion);
        Assert.True(JsonElement.DeepEquals(envelope.Arguments, argumentsDocument.RootElement));
    }

    [Fact]
    public void Deserialize_keeps_valid_json_shape_failures_distinct_from_invalid_json()
    {
        var envelope = ToolArgumentsCodec.Deserialize("[]"u8.ToArray());

        Assert.False(envelope.IsObject);
        Assert.ThrowsAny<JsonException>(() => ToolArgumentsCodec.Deserialize("{"u8.ToArray()));
    }

    [Theory]
    [InlineData("call_2", "read", "1.0.0", "{\"path\":\"a.cs\"}")]
    [InlineData("call_1", "write", "1.0.0", "{\"path\":\"a.cs\"}")]
    [InlineData("call_1", "read", "2.0.0", "{\"path\":\"a.cs\"}")]
    [InlineData("call_1", "read", "1.0.0", "{\"path\":\"b.cs\"}")]
    public void Matches_returns_false_when_identity_or_arguments_differ(
        string providerCallId,
        string toolId,
        string toolVersion,
        string expectedArgumentsJson)
    {
        using var persistedArguments = JsonDocument.Parse("{\"path\":\"a.cs\"}");
        using var expectedArguments = JsonDocument.Parse(expectedArgumentsJson);
        var serialized = ToolArgumentsCodec.Serialize(
            "call_1",
            "read",
            "1.0.0",
            persistedArguments.RootElement);
        var envelope = ToolArgumentsCodec.Deserialize(serialized);

        Assert.False(envelope.Matches(
            providerCallId,
            toolId,
            toolVersion,
            expectedArguments.RootElement));
    }
}
