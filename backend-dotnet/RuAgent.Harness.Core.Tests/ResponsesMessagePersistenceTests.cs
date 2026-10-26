using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core.Tests;

public sealed class ResponsesMessagePersistenceTests
{
    [Theory]
    [InlineData("commentary")]
    [InlineData("final_answer")]
    public void Assistant_step_codec_retains_phase_metadata_for_text_only_messages(string phase)
    {
        var json = new JsonObject
        {
            ["text"] = "Done.", ["tool_calls"] = new JsonArray(), ["finish_reason"] = "stop", ["estimated_input_tokens"] = 1,
            ["responses_messages"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["id"] = "msg_probe", ["role"] = "assistant", ["phase"] = phase,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Done." }),
            }),
        };

        var step = AssistantStepJsonCodec.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString()));
        var roundTrip = JsonNode.Parse(AssistantStepJsonCodec.Serialize(step))!.AsObject();

        Assert.True(roundTrip.ContainsKey("responses_messages"));
        Assert.Equal(phase, roundTrip["responses_messages"]![0]!["phase"]!.GetValue<string>());
    }
    [Fact]
    public void Legacy_payloads_remain_valid_without_inventing_phase_metadata()
    {
        var step = AssistantStepJsonCodec.Deserialize(Encoding.UTF8.GetBytes(
            """{"text":"Done.","tool_calls":[],"finish_reason":"stop","estimated_input_tokens":1}"""));
        Assert.Null(step.ResponsesMessages);
        Assert.DoesNotContain("responses_messages", Encoding.UTF8.GetString(AssistantStepJsonCodec.Serialize(step)));
    }

    [Theory]
    [InlineData("unknown", "Done.")]
    [InlineData("final_answer", "Different")]
    public void Invalid_phase_or_mismatched_text_is_rejected_by_the_persistence_boundary(string phase, string text)
    {
        var node = JsonNode.Parse("""{"text":"Done.","tool_calls":[],"finish_reason":"stop","estimated_input_tokens":1,"responses_messages":[{"type":"message","id":"msg_1","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"Done."}]}]}""")!;
        node["responses_messages"]![0]!["phase"] = phase;
        node["responses_messages"]![0]!["content"]![0]!["text"] = text;
        Assert.Throws<InvalidDataException>(() => AssistantStepJsonCodec.Deserialize(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Fact]
    public void Message_metadata_clones_inputs_and_rejects_duplicate_identities_and_excess_count()
    {
        var message = JsonNode.Parse("""{"type":"message","id":"msg_1","role":"assistant","phase":"commentary","content":[{"type":"output_text","text":"Checking."}]}""")!.AsObject();
        var metadata = new ResponsesAssistantMessages([message]);
        message["phase"] = "final_answer";
        Assert.True(metadata.IsCommentaryOnly);
        Assert.Throws<ArgumentException>(() => new ResponsesAssistantMessages([message, message]));
        Assert.Throws<ArgumentException>(() => new ResponsesAssistantMessages(Enumerable.Repeat(message, ResponsesAssistantMessages.MaximumMessages + 1).ToArray()));
    }
}
