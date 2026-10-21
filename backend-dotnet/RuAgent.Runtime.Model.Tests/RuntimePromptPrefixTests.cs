using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Model.Tests;

public sealed class RuntimePromptPrefixTests
{
    [Theory]
    [InlineData("chat_completions", "messages", "system")]
    [InlineData("openai_responses", "input", "instructions")]
    [InlineData("anthropic_messages", "messages", "system")]
    public void Changing_runtime_data_keeps_instructions_and_retained_history_as_a_stable_prefix(
        string protocol, string messageField, string systemField)
    {
        using var http = new HttpClient();
        IModelRequestBodyProjector adapter = protocol switch
        {
            "chat_completions" => new OpenAiCompatibleModelProvider(http),
            "openai_responses" => new OpenAiResponsesModelProvider(http),
            _ => new AnthropicMessagesModelProvider(http),
        };
        var capture = new CaptureProvider(adapter);
        var service = new ModelStepService(capture);
        var first = service.PrepareRequest(Request(protocol, "1 older result omitted"));
        var second = service.PrepareRequest(Request(protocol, "2 older results omitted; updated workspace activity"));
        var before = JsonNode.Parse(capture.Bodies[0])!;
        var after = JsonNode.Parse(capture.Bodies[1])!;
        var beforeMessages = before[messageField]!.AsArray();
        var afterMessages = after[messageField]!.AsArray();
        string system = protocol == "chat_completions"
            ? afterMessages[0]!["content"]!.GetValue<string>()
            : after[systemField]!.GetValue<string>();

        Assert.DoesNotContain("updated workspace activity", system);
        Assert.Equal(beforeMessages.Count, afterMessages.Count);
        for (int i = 0; i < beforeMessages.Count - 2; i++)
            Assert.True(JsonNode.DeepEquals(beforeMessages[i], afterMessages[i]));
        Assert.Contains("2 older results omitted", afterMessages[^2]!["content"]!.GetValue<string>());
        Assert.Contains(SystemPromptComposer.UntrustedDataWarning, afterMessages[^2]!["content"]!.GetValue<string>());
        Assert.Equal("CURRENT USER REQUEST", afterMessages[^1]!["content"]!.GetValue<string>());
        Assert.NotEqual(first.SerializedBodySha256, second.SerializedBodySha256);
        Assert.True(second.EstimatedInputTokens > first.EstimatedInputTokens);
    }

    [Fact]
    public async Task Runtime_receipt_still_binds_the_changing_data_while_the_system_prefix_stays_stable()
    {
        using var http = new HttpClient();
        var capture = new CaptureProvider(new OpenAiCompatibleModelProvider(http));
        var service = new ModelStepService(capture);
        var one = await service.RunPreparedAsync(service.PrepareRequest(Request("chat_completions", "old runtime")), null, default);
        var two = await service.RunPreparedAsync(service.PrepareRequest(Request("chat_completions", "new runtime")), null, default);

        Assert.Equal(one.PromptReceipt.PromptSha256, two.PromptReceipt.PromptSha256);
        Assert.NotEqual(one.PromptReceipt.BlockDigests.Single(block => block.BlockId == "runtime").Sha256,
            two.PromptReceipt.BlockDigests.Single(block => block.BlockId == "runtime").Sha256);
    }

    [Fact]
    public void Text_selection_keeps_its_inline_runtime_context()
    {
        using var http = new HttpClient();
        var capture = new CaptureProvider(new AnthropicMessagesModelProvider(http));
        new ModelStepService(capture).PrepareRequest(Request("anthropic_messages", "legacy runtime sentinel", native: false));
        var body = JsonNode.Parse(Assert.Single(capture.Bodies))!;
        Assert.Contains("legacy runtime sentinel", body["system"]!.GetValue<string>());
        Assert.Equal("CURRENT USER REQUEST", body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
    }

    private static ModelGenerationRequest Request(string protocol, string runtime, bool native = true)
    {
        var context = new SystemPromptContext(SystemPromptContext.PromptContextSchemaVersion, "ru-agent", "standard",
            new PromptEnvironment("fixture-model", "windows", "vi-VN", "Asia/Bangkok", "2026-10-21", "fixture"), [], [],
            new PromptRuntimeContext([new PromptReference("tool_result_omitted", "untrusted omitted results", runtime)]));
        var bootstrap = new ProviderBootstrapData("fixture", protocol, new Uri("https://provider.invalid/v1"),
            "fixture-model", "synthetic", maximumOutputTokens: 8192,
            toolCallingMode: native ? ProviderToolCallingMode.Native : ProviderToolCallingMode.TextSelection,
            allowNativeResponses: true, allowNativeAnthropic: true, contextWindowTokens: 128_000);
        JsonObject[] history = [new() { ["role"] = "user", ["content"] = "earlier user message" },
            new() { ["role"] = "assistant", ["content"] = "earlier assistant response" }];
        JsonObject[] anthropicHistory = [new() { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "earlier user message" }) },
            new() { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "earlier assistant response" }) }];
        return new ModelGenerationRequest("CURRENT USER REQUEST",
            [new ModelTurn("user", "earlier user message"), new ModelTurn("assistant", "earlier assistant response")],
            context, new ModelGenerationSettings(), bootstrap,
            responsesInputItems: protocol == "openai_responses" ? history : null,
            anthropicMessages: protocol == "anthropic_messages" ? anthropicHistory : null);
    }

    private sealed class CaptureProvider(IModelRequestBodyProjector projector) : IModelProvider, IModelRequestBodyProjector
    {
        public List<string> Bodies { get; } = [];
        public string ProjectRequestBody(ModelRequest request)
        {
            string body = projector.ProjectRequestBody(request);
            Bodies.Add(body);
            return body;
        }
        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelDelta("done", [], completed: true, finishReason: "stop");
        }
    }
}
