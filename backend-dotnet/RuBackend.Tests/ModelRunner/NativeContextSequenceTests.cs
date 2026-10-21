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

public sealed class NativeContextSequenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Next_request_preserves_previous_messages_through_tool_output_and_restored_context(bool change)
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        InProcessModelStepRunner Runner() => new(new ModelStepService(new OpenAiCompatibleModelProvider(http)))
        {
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(new ProviderBootstrapData("fixture", "chat_completions",
                new Uri("https://provider.invalid/v1/chat/completions"), "fixture", "synthetic")),
        };
        var definition = AgentToolDefinitionCatalog.Find("read", "1.0.0")!;
        var capability = new PromptCapability(definition.ToolId, definition.ToolVersion, definition.Description, definition.Scope,
            AgentToolDefinitionCatalog.CanonicalSchemaJson(definition.InputSchema), AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema), null, "enabled");
        var promptContext = new SystemPromptContext(2, "ru-agent", "standard",
            new PromptEnvironment("fixture", "windows", "vi-VN", "UTC", "2026-10-21", "fixture"), [], [capability], new PromptRuntimeContext([]));
        var user = ModelContextEntry.Text("user", 2, ModelContextRole.User, "Read file.txt");
        ModelContextEntry State(string text) => ModelContextEntry.Text(SessionWorkState.ContextSourceId, 1, ModelContextRole.User, text);
        ModelStepRequest Request(params ModelContextEntry[] entries) => new(new RunId("run_" + new string('a', 32)),
            new ModelStepId("step_" + new string('b', 32)), new ModelContextSnapshot(entries, ReadOnlyMemory<byte>.Empty),
            promptContext, AgentPermissionPreset.Standard, 1, new string('c', 64));
        var runner = Runner();
        var first = Request(State("original workflow"), user);
        var prepared = await runner.PrepareAsync(first, default);
        Assert.NotNull(prepared.RuntimeContextSnapshot);
        var result = await runner.RunStepAsync(first.WithPreparation(prepared), _ => ValueTask.CompletedTask, default);
        var snapshot = ModelContextEntry.Text(RuntimeContextSnapshot.SourcePrefix + "recorded", 3, ModelContextRole.User, prepared.RuntimeContextSnapshot!);
        var assistant = ModelContextEntry.Assistant("assistant", 4, result.Step);
        var receipt = new ToolExecutionReceipt(new ExecutionId("exec_" + new string('d', 32)),
            new ToolCallId("call_" + new string('e', 32)), ToolExecutionOutcome.Succeeded, "ok", null, false, providerCallId: "read_1");
        var tool = ModelContextEntry.ToolResult("result", 5, receipt, "file contents");

        // A fresh runner sees only restored durable history, not any process-local cache.
        runner = Runner();
        var second = Request(State(change ? "updated workflow" : "original workflow"), user, snapshot, assistant, tool);
        var next = await runner.PrepareAsync(second, default);
        Assert.Equal(change, next.RuntimeContextSnapshot is not null);
        await runner.RunStepAsync(second.WithPreparation(next), _ => ValueTask.CompletedTask, default);
        var before = JsonNode.Parse(handler.Bodies[0])!["messages"]!.AsArray();
        var after = JsonNode.Parse(handler.Bodies[1])!["messages"]!.AsArray();
        Assert.True(after.Count > before.Count);
        for (int i = 0; i < before.Count; i++) Assert.True(JsonNode.DeepEquals(before[i], after[i]));
        Assert.Single(before, message => message!["content"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            && message["content"]!.GetValue<string>() == "Read file.txt");
        Assert.DoesNotContain(after, message => message!["content"]?.ToString() == "Continue from the durable tool results.");
        if (change) Assert.Contains("updated workflow", after[^1]!["content"]!.GetValue<string>());
        else Assert.Equal("tool", after[^1]!["role"]!.GetValue<string>());
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var json = Bodies.Count == 1
                ? """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"read_1","type":"function","function":{"name":"read","arguments":"{\"path\":\"file.txt\"}"}}]},"finish_reason":"tool_calls"}]}"""
                : """{"choices":[{"delta":{"content":"done"},"finish_reason":"stop"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: " + json + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
