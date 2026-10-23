using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

/// <summary>
/// ADR 0026: a tool result's image reaches each protocol in its native shape. Two
/// near-4 MiB images are used on purpose: together they exceed the 8 MiB text history
/// bound, which must measure text only.
/// </summary>
public sealed class ToolImageRequestBodyTests
{
    private static readonly ModelImage First = NearMaximumImage("image/png", 1);
    private static readonly ModelImage Second = NearMaximumImage("image/jpeg", 2);

    [Fact]
    public void Chat_completions_follows_the_tool_batch_with_one_user_message_of_images()
    {
        var body = Parse(new OpenAiCompatibleModelProvider(new HttpClient()).ProjectRequestBody(Request(Turns())));

        var messages = body["messages"]!.AsArray();
        var roles = messages.Select(message => message!["role"]!.GetValue<string>()).ToArray();
        Assert.Equal(["system", "user", "assistant", "tool", "tool", "user", "user"], roles);
        // Tool messages stay text: the protocol accepts nothing else there.
        Assert.Equal(JsonValueKind.String, messages[3]!["content"]!.GetValueKind());
        Assert.Equal(JsonValueKind.String, messages[4]!["content"]!.GetValueKind());
        var images = messages[5]!["content"]!.AsArray();
        Assert.Equal(
            ["text", "image_url", "text", "image_url"],
            images.Select(part => part!["type"]!.GetValue<string>()).ToArray());
        Assert.Equal("Image returned by tool call call_1:", images[0]!["text"]!.GetValue<string>());
        Assert.Equal(First.DataUrl, images[1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal(Second.DataUrl, images[3]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("hello", messages[6]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Responses_function_call_output_carries_input_text_then_input_image()
    {
        var turns = Turns();
        var items = new List<JsonObject>
        {
            new() { ["role"] = "user", ["content"] = "look at the screenshots" },
            new() { ["type"] = "function_call", ["call_id"] = "call_1", ["name"] = "read", ["arguments"] = "{\"path\":\"a.png\"}" },
            new() { ["type"] = "function_call", ["call_id"] = "call_2", ["name"] = "read", ["arguments"] = "{\"path\":\"b.jpg\"}" },
            new() { ["type"] = "function_call_output", ["call_id"] = "call_1", ["output"] = ToolImageProjection.ResponsesOutput(turns[2]) },
            new() { ["type"] = "function_call_output", ["call_id"] = "call_2", ["output"] = ToolImageProjection.ResponsesOutput(turns[3]) },
        };

        var body = Parse(new OpenAiResponsesModelProvider(new HttpClient()).ProjectRequestBody(
            Request(turns, responsesInputItems: items)));

        var output = body["input"]!.AsArray()[3]!["output"]!.AsArray();
        Assert.Equal("input_text", output[0]!["type"]!.GetValue<string>());
        Assert.Equal("{\"path\":\"a.png\"}", output[0]!["text"]!.GetValue<string>());
        Assert.Equal("input_image", output[1]!["type"]!.GetValue<string>());
        Assert.Equal(First.DataUrl, output[1]!["image_url"]!.GetValue<string>());
        Assert.Equal(Second.DataUrl, body["input"]!.AsArray()[4]!["output"]!.AsArray()[1]!["image_url"]!.GetValue<string>());
    }

    [Fact]
    public void Responses_turn_fallback_projects_the_same_array()
    {
        var body = Parse(new OpenAiResponsesModelProvider(new HttpClient()).ProjectRequestBody(
            Request(Turns(), settings: new JsonObject { ["model_tools"] = new JsonArray() })));

        var outputs = body["input"]!.AsArray().Where(item => item!["type"]?.GetValue<string>() == "function_call_output").ToArray();
        Assert.Equal(2, outputs.Length);
        Assert.Equal(Second.DataUrl, outputs[1]!["output"]!.AsArray()[1]!["image_url"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_tool_result_content_is_a_text_block_then_a_base64_image_block()
    {
        var turns = Turns();
        var messages = new List<JsonObject>
        {
            new() { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "look" }) },
            new()
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "tool_use", ["id"] = "call_1", ["name"] = "read", ["input"] = new JsonObject { ["path"] = "a.png" } },
                    new JsonObject { ["type"] = "tool_use", ["id"] = "call_2", ["name"] = "read", ["input"] = new JsonObject { ["path"] = "b.jpg" } }),
            },
            new()
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    ToolResult("call_1", turns[2]),
                    ToolResult("call_2", turns[3])),
            },
        };

        var body = Parse(new AnthropicMessagesModelProvider(new HttpClient()).ProjectRequestBody(
            Request(turns, anthropicMessages: messages, maximumOutputTokens: 1024)));

        var result = body["messages"]!.AsArray()[2]!["content"]!.AsArray()[0]!["content"]!.AsArray();
        Assert.Equal("text", result[0]!["type"]!.GetValue<string>());
        Assert.Equal("image", result[1]!["type"]!.GetValue<string>());
        Assert.Equal("base64", result[1]!["source"]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", result[1]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal(First.Base64Data, result[1]!["source"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_history_rejects_an_image_block_outside_a_tool_result()
    {
        var messages = new List<JsonObject>
        {
            new()
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = "AAAA" },
                }),
            },
        };

        Assert.Throws<ArgumentException>(() => Request([], anthropicMessages: messages, maximumOutputTokens: 1024));
    }

    [Fact]
    public void History_carries_at_most_the_context_image_bound()
    {
        var small = new ModelImage("image/png", "AAAA");
        var calls = Enumerable.Range(1, ToolOutputImage.MaximumInContext + 1)
            .Select(index => new ModelFunctionCall($"call_{index}", "read", JsonDocument.Parse("{}").RootElement))
            .ToArray();
        var turns = new List<ModelTurn> { new("user", "look"), new("assistant", string.Empty, calls) };
        turns.AddRange(calls.Select(call => new ModelTurn("tool", "{}", toolCallId: call.Id, toolResultCode: "ok", images: [small])));

        var error = Assert.Throws<ArgumentException>(() => Request(turns));

        Assert.Contains("too many images", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_tool_turns_carry_images()
    {
        Assert.Throws<ArgumentException>(() => new ModelTurn("user", "look", images: [First]));
    }

    [Fact]
    public void All_images_counts_attachments_and_history_images()
    {
        var request = Request(Turns());

        Assert.Equal(2, request.AllImages.Count());
    }

    private static JsonObject ToolResult(string id, ModelTurn turn) => new()
    {
        ["type"] = "tool_result",
        ["tool_use_id"] = id,
        ["content"] = ToolImageProjection.AnthropicContent(turn),
        ["is_error"] = false,
    };

    private static List<ModelTurn> Turns()
    {
        var calls = new[]
        {
            new ModelFunctionCall("call_1", "read", JsonDocument.Parse("{\"path\":\"a.png\"}").RootElement),
            new ModelFunctionCall("call_2", "read", JsonDocument.Parse("{\"path\":\"b.jpg\"}").RootElement),
        };
        return
        [
            new ModelTurn("user", "look at the screenshots"),
            new ModelTurn("assistant", string.Empty, calls),
            new ModelTurn("tool", "{\"path\":\"a.png\"}", toolCallId: "call_1", toolResultCode: "ok", images: [First]),
            new ModelTurn("tool", "{\"path\":\"b.jpg\"}", toolCallId: "call_2", toolResultCode: "ok", images: [Second]),
        ];
    }

    private static ModelRequest Request(
        IReadOnlyList<ModelTurn> turns,
        JsonObject? settings = null,
        IReadOnlyList<JsonObject>? responsesInputItems = null,
        IReadOnlyList<JsonObject>? anthropicMessages = null,
        int? maximumOutputTokens = null) => new(
        "test-model",
        "system prompt",
        "hello",
        turns,
        settings ?? new JsonObject(),
        new Uri("https://provider.invalid/v1/endpoint"),
        "provider-secret",
        maximumOutputTokens,
        responsesInputItems: responsesInputItems,
        nativeToolCalling: true,
        anthropicMessages: anthropicMessages);

    private static ModelImage NearMaximumImage(string mediaType, byte fill)
    {
        var bytes = new byte[ToolOutputImage.MaximumBytes - 16];
        Array.Fill(bytes, fill);
        return new ModelImage(mediaType, Convert.ToBase64String(bytes));
    }

    private static JsonObject Parse(string body) => JsonNode.Parse(body)!.AsObject();
}
