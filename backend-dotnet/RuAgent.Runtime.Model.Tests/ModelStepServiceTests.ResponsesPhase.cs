using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Model.Tests;

public sealed partial class ModelStepServiceTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1/responses", "gpt-5.3-codex", true)]
    [InlineData("https://provider.invalid/v1/responses", "gpt-5.3-codex", false)]
    [InlineData("https://api.openai.com:8443/v1/responses", "gpt-5.3-codex", false)]
    [InlineData("http://api.openai.com/v1/responses", "gpt-5.3-codex", false)]
    [InlineData("https://api.openai.com/v1/responses", "custom-codex-alias", false)]
    [InlineData("https://api.openai.com/v1/responses", "gpt-5.6-luna", false)]
    public async Task Prompt_recipe_uses_only_confirmed_official_route(string endpoint, string model, bool expected)
    {
        var provider = new FakeModelProvider(new ModelDelta("Done.", [], true, "stop"));
        var bootstrap = new ProviderBootstrapData("fixture", ProviderProtocols.OpenAiResponses, new Uri(endpoint), model,
            "synthetic", toolCallingMode: ProviderToolCallingMode.Native, allowNativeResponses: true);
        await new ModelStepService(provider).RunAsync(Request(bootstrap: bootstrap), default);
        Assert.Equal(expected, provider.Requests.Single().SystemPrompt.Contains("For this coding workflow, keep narration concise", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Native_responses_metadata_survives_the_model_service()
    {
        var messages = new ResponsesAssistantMessages([new JsonObject
        {
            ["type"] = "message", ["id"] = "msg_final", ["role"] = "assistant", ["phase"] = "final_answer",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Done." }),
        }]);
        var provider = new FakeModelProvider(new ModelDelta("Done.", [], true, "stop", responsesMessages: messages));
        var result = await new ModelStepService(provider).RunAsync(Request(bootstrap: ResponsesNativeBootstrap()), default);
        Assert.Same(messages, result.ResponsesMessages);
        Assert.Equal("Done.", result.Text);
    }

    [Theory]
    [InlineData("provider_continuation_too_large", "provider_continuation_too_large")]
    [InlineData("response_too_large", "protocol_failure")]
    public async Task Oversized_response_is_not_a_retryable_generation_failure(string providerCode, string expectedCode)
    {
        var result = await Assert.ThrowsAsync<ModelStepException>(() => new ModelStepService(
            new ThrowingProvider(new ModelProviderException(providerCode, false)))
                .RunAsync(Request(bootstrap: ResponsesNativeBootstrap()), default));
        Assert.Equal(expectedCode, result.Code);
        Assert.False(result.Retryable);
    }
}
