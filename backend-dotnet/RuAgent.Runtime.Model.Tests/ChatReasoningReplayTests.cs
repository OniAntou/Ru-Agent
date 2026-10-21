using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Model.Tests;

public sealed class ChatReasoningReplayTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Full_private_reasoning_is_separate_from_the_display_excerpt_and_requires_a_pinned_capability(bool enabled)
    {
        var service = new ModelStepService(new Provider(3));
        var result = await service.RunPreparedAsync(service.PrepareRequest(Request(enabled)), null, default);
        Assert.True(result.ReasoningText!.Length <= 8000);
        if (enabled)
        {
            Assert.Equal("chat", result.ProviderContinuation!.Provider);
            Assert.Equal(new string('r', 36_000), result.ProviderContinuation.OutputItems[0]["reasoning_content"]!.GetValue<string>());
            Assert.Equal("read_1", result.ProviderContinuation.OutputItems[0]["tool_calls"]![0]!["id"]!.GetValue<string>());
        }
        else Assert.Null(result.ProviderContinuation);
    }

    [Fact]
    public async Task Oversized_private_reasoning_fails_by_name_without_promoting_calls()
    {
        var service = new ModelStepService(new Provider(12));
        var error = await Assert.ThrowsAsync<ModelStepException>(() =>
            service.RunPreparedAsync(service.PrepareRequest(Request(true)), null, default));
        Assert.Equal("provider_continuation_too_large", error.Code);
        Assert.False(error.Retryable);
    }

    private static ModelGenerationRequest Request(bool enabled)
    {
        var definition = AgentToolDefinitionCatalog.Find("read", "1.0.0")!;
        var capability = new PromptCapability(definition.ToolId, definition.ToolVersion, definition.Description, definition.Scope,
            AgentToolDefinitionCatalog.CanonicalSchemaJson(definition.InputSchema), AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema), null, "enabled");
        var context = new SystemPromptContext(2, "ru-agent", "standard",
            new PromptEnvironment("fixture", "windows", "vi-VN", "UTC", "2026-10-21", "fixture"), [], [capability], new PromptRuntimeContext([]));
        return new ModelGenerationRequest("Read file", [], context,
            new ModelGenerationSettings(new JsonArray(new JsonObject
            {
                ["tool_id"] = "read", ["tool_version"] = "1.0.0", ["scopes"] = new JsonArray("workspace.read"),
                ["availability"] = "executable", ["model_enabled"] = true, ["capability_class"] = "tool",
                ["verification_required"] = false,
                ["input_schema"] = definition.InputSchema,
            })), new ProviderBootstrapData("fixture", "chat_completions",
                new Uri("https://provider.invalid/v1/chat/completions"), "fixture", "synthetic", replayChatReasoning: enabled));
    }

    private sealed class Provider(int chunks) : IModelProvider, IModelRequestBodyProjector
    {
        public string ProjectRequestBody(ModelRequest request)
        {
            using var http = new HttpClient();
            return new OpenAiCompatibleModelProvider(http).ProjectRequestBody(request);
        }

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            for (int i = 0; i < chunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ModelDelta(null, [], false, null, reasoning: new string('r', 12_000));
            }
            yield return new ModelDelta(null, [new ModelToolFragment("read_1", "read", "{\"path\":\"file.txt\"}")], true, "tool_calls");
        }
    }
}
