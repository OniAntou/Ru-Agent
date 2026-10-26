using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class ResponsesPhaseReplayTests
{
    [Fact]
    public void Tool_continuation_keeps_assistant_message_phase_in_output_order()
    {
        var request = new ModelRequest("test-model", "system", "user", [],
            new JsonObject { ["model_tools"] = new JsonArray() }, new Uri("http://127.0.0.1:1"), "synthetic-key", nativeToolCalling: true);
        var output = JsonNode.Parse("""[{"type":"message","id":"msg_probe","role":"assistant","phase":"commentary","content":[{"type":"output_text","text":"Inspecting."}]},{"type":"function_call","id":"fc_probe","call_id":"call_probe","name":"read_file","arguments":"{\"path\":\"example.txt\"}"}]""")!.AsArray();
        var build = typeof(OpenAiResponsesModelProvider).GetMethod("BuildContinuation", BindingFlags.NonPublic | BindingFlags.Static)!;

        var continuation = (ProviderContinuation)build.Invoke(null, [request, output, true])!;

        Assert.Equal(new[] { "message", "function_call" }, continuation.OutputItems.Select(item => item["type"]!.GetValue<string>()));
        Assert.Equal("commentary", continuation.OutputItems[0]["phase"]!.GetValue<string>());
    }
    [Fact]
    public void Public_message_metadata_accepts_its_byte_bound_and_rejects_one_extra_byte_without_retry()
    {
        var request = new ModelRequest("test-model", "system", "user", [], new JsonObject(),
            new Uri("http://127.0.0.1:1"), "synthetic", nativeToolCalling: true);
        var message = JsonNode.Parse("""{"type":"message","id":"msg_1","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":""}]}""")!.AsObject();
        var output = new JsonArray(message);
        var overhead = Encoding.UTF8.GetByteCount(output.ToJsonString());
        message["content"]![0]!["text"] = new string('a', ResponsesAssistantMessages.MaximumBytes - overhead);
        var read = typeof(OpenAiResponsesModelProvider).GetMethod("ReadAssistantMessages", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsType<ResponsesAssistantMessages>(read.Invoke(null, [request, output]));
        message["content"]![0]!["text"] = new string('a', ResponsesAssistantMessages.MaximumBytes - overhead + 1);
        var error = Assert.Throws<TargetInvocationException>(() => read.Invoke(null, [request, output]));
        var providerError = Assert.IsType<ModelProviderException>(error.InnerException);
        Assert.Equal("response_too_large", providerError.Code);
        Assert.False(providerError.Retryable);
    }
}
