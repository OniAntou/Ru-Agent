using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using RuBackend.Harness.ModelRunner;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

public sealed class ResponsesPhaseHistoryTests
{
    [Theory]
    [InlineData("commentary")]
    [InlineData("final_answer")]
    public async Task Fresh_runner_replays_codec_restored_text_only_phase_exactly_once(string phase)
    {
        var handler = new Handler(phase);
        using var http = new HttpClient(handler);
        InProcessModelStepRunner Runner() => new(new ModelStepService(new OpenAiResponsesModelProvider(http)))
        {
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(new ProviderBootstrapData("fixture", "openai_responses",
                new Uri("https://provider.invalid/v1/responses"), "fixture", "synthetic",
                toolCallingMode: ProviderToolCallingMode.Native, allowNativeResponses: true)),
        };
        ModelStepRequest Request(params ModelContextEntry[] entries) => new(new RunId("run_" + new string('a', 32)),
            new ModelStepId("step_" + new string('b', 32)), new ModelContextSnapshot(entries, ReadOnlyMemory<byte>.Empty),
            SystemPromptContext.Empty, AgentPermissionPreset.Standard, 1, new string('c', 64));
        var user = ModelContextEntry.Text("user", 1, ModelContextRole.User, "Do it");
        var first = await Runner().RunStepAsync(Request(user), _ => ValueTask.CompletedTask, default);
        Assert.NotNull(first.Step.ResponsesMessages);
        var restored = AssistantStepJsonCodec.Deserialize(AssistantStepJsonCodec.Serialize(first.Step));
        await Runner().RunStepAsync(Request(user, ModelContextEntry.Assistant("assistant", 2, restored),
            ModelContextEntry.Text("continue", 3, ModelContextRole.User, "Continue")), _ => ValueTask.CompletedTask, default);
        var input = JsonNode.Parse(handler.Bodies[1])!["input"]!.AsArray();
        var replayed = Assert.Single(input, i => i?["type"]?.GetValue<string>() == "message");
        Assert.Equal(phase, replayed!["phase"]!.GetValue<string>());
        Assert.Equal("Working.", replayed["content"]![0]!["text"]!.GetValue<string>());
        Assert.DoesNotContain(input, i => i?["role"]?.GetValue<string>() == "assistant" && i?["type"] is null);
        Assert.All(input.Where(i => i?["role"]?.GetValue<string>() == "user"), i => Assert.Null(i!["phase"]));
    }

    [Fact]
    public void Tool_batch_uses_messages_from_continuation_without_duplicate_flattened_text()
    {
        var message = JsonNode.Parse("""{"type":"message","id":"msg_1","role":"assistant","phase":"commentary","content":[{"type":"output_text","text":"Checking."}]}""")!.AsObject();
        var call = JsonNode.Parse("""{"type":"function_call","id":"fc_1","call_id":"call_1","name":"read","arguments":"{}"}""")!.AsObject();
        var continuation = new ProviderContinuation("openai", "openai_responses", "fixture", 1, [message, call]);
        var turns = new[] { new ModelTurn("assistant", "Checking.", [new ModelFunctionCall("call_1", "read", System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone())]),
            new ModelTurn("tool", "content", toolCallId: "call_1", toolResultCode: "ok") };
        var projected = OpenAiResponsesHistoryProjector.Project(turns, [continuation], "fixture");
        Assert.Equal(new[] { "message", "function_call", "function_call_output" }, projected.Select(i => i["type"]!.GetValue<string>()));
        Assert.Equal("commentary", projected[0]["phase"]!.GetValue<string>());
    }

    private sealed class Handler(string phase) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var response = new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject
            {
                ["model"] = "fixture", ["status"] = "completed", ["output"] = new JsonArray(new JsonObject
                {
                    ["type"] = "message", ["id"] = "msg_1", ["role"] = "assistant", ["phase"] = phase,
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Working." }),
                }),
            }};
            return new(HttpStatusCode.OK) { Content = new StringContent($"event: response.completed\ndata: {response.ToJsonString()}\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
