using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class ResponsesMessageStreamingTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Checking. ")]
    [InlineData("Checking. Done.")]
    public async Task Completed_messages_keep_individual_phase_and_emit_text_exactly_once(string prefix)
    {
        var output = new JsonArray(Message("msg_1", "Checking. ", "commentary"), Message("msg_2", "Done.", "final_answer"));
        var deltas = await Generate(output, prefix);
        Assert.Equal("Checking. Done.", string.Concat(deltas.Select(d => d.Text)));
        var messages = Assert.IsType<ResponsesAssistantMessages>(deltas[^1].ResponsesMessages);
        Assert.Equal(new[] { "commentary", "final_answer" }, messages.Messages.Select(m => m["phase"]!.GetValue<string>()));
        Assert.False(messages.IsCommentaryOnly);
        Assert.Null(deltas[^1].ProviderContinuation);
    }

    [Fact]
    public async Task Null_phase_is_preserved_and_does_not_force_commentary_recovery()
    {
        var deltas = await Generate(new JsonArray(Message("msg_1", "Done.", null)), "");
        var messages = deltas[^1].ResponsesMessages!;
        Assert.True(messages.Messages[0].ContainsKey("phase"));
        Assert.Null(messages.Messages[0]["phase"]);
        Assert.False(messages.IsCommentaryOnly);
    }

    [Theory]
    [InlineData("phase", "unknown")]
    [InlineData("role", "user")]
    public async Task Invalid_phased_messages_are_rejected(string field, string value)
    {
        var message = Message("msg_1", "Done.", "final_answer");
        message[field] = value;
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => Generate(new JsonArray(message), ""));
        Assert.Equal("malformed_payload", error.Code);
    }

    [Fact]
    public async Task Streamed_text_must_agree_with_completed_message_text()
    {
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => Generate(
            new JsonArray(Message("msg_1", "Done.", "final_answer")), "different"));
        Assert.Equal("malformed_payload", error.Code);
    }

    [Fact]
    public async Task Tool_reply_retains_original_message_order_without_losing_phase()
    {
        var output = new JsonArray(Message("msg_1", "Checking.", "commentary"), Call());
        var result = await Generate(output, "Checking.");
        Assert.Equal("Checking.", string.Concat(result.Select(d => d.Text)));
        Assert.Equal(new[] { "message", "function_call" }, result[^1].ProviderContinuation!.OutputItems.Select(i => i["type"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Oversized_phased_tool_continuation_has_a_bounded_non_retryable_code()
    {
        var output = new JsonArray(Message("msg_1", new string('a', ProviderContinuation.MaximumBytes), "commentary"), Call());
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => Generate(output, ""));
        Assert.Equal("provider_continuation_too_large", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public void Direct_request_projection_preserves_phase_without_inventing_user_phase()
    {
        var messages = new ResponsesAssistantMessages([Message("msg_1", "Checking.", "commentary")]);
        var request = Request([new ModelTurn("user", "Do it"), new ModelTurn("assistant", "Checking.", responsesMessages: messages)]);
        var provider = new OpenAiResponsesModelProvider(new HttpClient());
        var body = JsonNode.Parse(provider.ProjectRequestBody(request))!;
        Assert.Equal("commentary", body["input"]![1]!["phase"]!.GetValue<string>());
        Assert.Null(body["input"]![0]!["phase"]);
    }

    private static JsonObject Message(string id, string text, string? phase) => new()
    {
        ["type"] = "message", ["id"] = id, ["role"] = "assistant", ["phase"] = phase,
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() }),
    };
    private static JsonObject Call() => new()
    {
        ["type"] = "function_call", ["id"] = "fc_1", ["call_id"] = "call_1", ["name"] = "read_file", ["arguments"] = "{}",
    };
    private static ModelRequest Request(IReadOnlyList<ModelTurn>? turns = null) => new("test-model", "system", "prompt", turns ?? [],
        new JsonObject { ["model_tools"] = new JsonArray() }, new Uri("http://127.0.0.1:1"), "synthetic",
        nativeToolCalling: true);
    private static async Task<List<ModelDelta>> Generate(JsonArray output, string prefix)
    {
        var sse = prefix.Length == 0 ? "" : Event("response.output_text.delta", new JsonObject { ["delta"] = prefix });
        for (var index = 0; index < output.Count; index++)
        {
            if (output[index] is not JsonObject item || item["type"]?.GetValue<string>() != "function_call") continue;
            var added = (JsonObject)item.DeepClone();
            added["arguments"] = "";
            sse += Event("response.output_item.added", new JsonObject { ["output_index"] = index, ["item"] = added });
            sse += Event("response.function_call_arguments.done", new JsonObject
            {
                ["output_index"] = index, ["item_id"] = item["id"]!.GetValue<string>(),
                ["arguments"] = item["arguments"]!.GetValue<string>(),
            });
        }
        sse += Event("response.completed", new JsonObject { ["response"] = new JsonObject
        {
            ["model"] = "test-model", ["status"] = "completed", ["output"] = output,
        }});
        using var http = new HttpClient(new Handler(sse));
        var deltas = new List<ModelDelta>();
        await foreach (var delta in new OpenAiResponsesModelProvider(http).GenerateAsync(Request(), default)) deltas.Add(delta);
        return deltas;
    }
    private static string Event(string name, JsonObject data)
    {
        data["type"] = name;
        return $"event: {name}\ndata: {data.ToJsonString()}\n\n";
    }
    private sealed class Handler(string sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
    }
}
