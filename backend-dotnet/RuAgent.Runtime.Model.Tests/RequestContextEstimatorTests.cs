using System.Text.Json.Nodes;
using Xunit;

namespace RuAgent.Runtime.Model.Tests;

public sealed class RequestContextEstimatorTests
{
    [Theory]
    [InlineData("messages", "system")]
    [InlineData("input", "instructions")]
    public void Counts_native_system_tools_and_message_bytes_independently(string input, string system)
    {
        var body = new JsonObject
        {
            [system] = new string('s', 400),
            ["tools"] = new JsonArray(new JsonObject { ["name"] = "sample", ["description"] = new string('t', 800) }),
            [input] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new string('m', 1200) }),
        };
        var first = RequestContextEstimator.Measure(body.ToJsonString())!;
        Assert.InRange(first.SystemPrompt, 100, 105);
        Assert.InRange(first.ToolDefinitions, 200, 225);
        Assert.InRange(first.Messages, 300, 325);
        body["tools"] = new JsonArray();
        var withoutTools = RequestContextEstimator.Measure(body.ToJsonString())!;
        Assert.Equal(0, withoutTools.ToolDefinitions);
        Assert.Equal(first.SystemPrompt, withoutTools.SystemPrompt);
        Assert.Equal(first.Messages, withoutTools.Messages);
    }

    [Fact]
    public void Chat_system_and_developer_turns_do_not_count_as_conversation_messages()
    {
        var counts = RequestContextEstimator.Measure("""{"messages":[{"role":"system","content":"system only"},{"role":"developer","content":"instructions"},{"role":"user","content":"hello"}]}""")!;
        Assert.True(counts.SystemPrompt > counts.Messages);
        Assert.Equal(0, counts.ToolDefinitions);
    }

    [Fact]
    public void Image_payload_size_does_not_masquerade_as_text_tokens()
    {
        var body = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["content"] = new string('A', 200_000) }) };
        var counts = RequestContextEstimator.Measure(body.ToJsonString(), 200_000, 1)!;
        Assert.InRange(counts.Messages, 4096, 4112);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Unsupported_projection_stays_unknown_without_failing_generation(string body) =>
        Assert.Null(RequestContextEstimator.Measure(body));
}
