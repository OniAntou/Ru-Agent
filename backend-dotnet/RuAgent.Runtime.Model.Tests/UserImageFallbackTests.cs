using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Model.Tests;

public sealed class UserImageFallbackTests
{
    private const string EncodedImage = "aW1hZ2UtY29udGVudC1tdXN0LW5vdC1iZS1zZW50";

    [Theory]
    [InlineData("chat_completions", "Please help with this screenshot")]
    [InlineData("openai_responses", "Please help with this screenshot")]
    [InlineData("anthropic_messages", "Please help with this screenshot")]
    [InlineData("chat_completions", "")]
    [InlineData("openai_responses", "")]
    [InlineData("anthropic_messages", "")]
    public async Task Text_only_model_continues_with_an_explicit_image_notice(string protocol, string prompt)
    {
        using var http = new HttpClient();
        var provider = new CapturingProvider(protocol, http);
        var service = new ModelStepService(provider);
        var bootstrap = Bootstrap(protocol, "stealth/glyph-cluster");
        var source = new ModelGenerationRequest(prompt, [], SystemPromptContext.Empty,
            new ModelGenerationSettings(), bootstrap, [new ModelImage("image/png", EncodedImage)]);

        var result = await service.RunAsync(source, CancellationToken.None);

        Assert.Equal("reply", result.Text);
        var request = Assert.IsType<ModelRequest>(provider.Received);
        Assert.Equal(bootstrap.Model, request.Model);
        Assert.Equal(bootstrap.Endpoint, request.Endpoint);
        Assert.Empty(request.Images);
        Assert.Contains("image contents are unavailable", request.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not infer", request.Prompt, StringComparison.OrdinalIgnoreCase);
        if (prompt.Length > 0) Assert.StartsWith(prompt, request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(EncodedImage, request.PreparedBodyJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"input_image\"", request.PreparedBodyJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"image_url\"", request.PreparedBodyJson!, StringComparison.Ordinal);
        Assert.Single(source.Images);
        // Retrying preparation leaves the source attachment intact and projects the same notice.
        Assert.Equal(request.PreparedBodyJson, service.PrepareRequest(source).Request.PreparedBodyJson);
    }

    [Theory]
    [InlineData("chat_completions")]
    [InlineData("openai_responses")]
    [InlineData("anthropic_messages")]
    public async Task Image_notice_is_sent_even_when_the_original_prompt_is_already_in_history(string protocol)
    {
        using var http = new HttpClient();
        var provider = new CapturingProvider(protocol, http);
        var service = new ModelStepService(provider);
        var source = new ModelGenerationRequest("question", [new ModelTurn("user", "question")],
            SystemPromptContext.Empty, new ModelGenerationSettings(), Bootstrap(protocol, "text-model"),
            [new ModelImage("image/png", EncodedImage)], promptInHistory: true);

        await service.RunAsync(source, default);

        Assert.False(provider.Received!.PromptInHistory);
        Assert.Contains("image contents are unavailable", provider.Received.PreparedBodyJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(EncodedImage, provider.Received.PreparedBodyJson!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("chat_completions", "gpt-4o", false)]
    [InlineData("openai_responses", "gpt-4o", false)]
    [InlineData("anthropic_messages", "gpt-4o", false)]
    [InlineData("chat_completions", "gateway/opaque-model", true)]
    [InlineData("openai_responses", "gateway/opaque-model", true)]
    [InlineData("anthropic_messages", "gateway/opaque-model", true)]
    public async Task Pinned_capability_overrides_name_hints_in_preparation_and_dispatch(
        string protocol, string model, bool accepts)
    {
        using var http = new HttpClient();
        var provider = new CapturingProvider(protocol, http);
        var service = new ModelStepService(provider);
        var source = new ModelGenerationRequest("question", [], SystemPromptContext.Empty,
            new ModelGenerationSettings(), Bootstrap(protocol, model, accepts),
            [new ModelImage("image/png", EncodedImage)]);

        await service.RunAsync(source, CancellationToken.None);

        var request = Assert.IsType<ModelRequest>(provider.Received);
        Assert.Equal(accepts, request.AcceptsImageInput);
        Assert.Equal(model, request.Model);
        if (accepts)
        {
            Assert.Single(request.Images);
            Assert.Contains(EncodedImage, request.PreparedBodyJson!, StringComparison.Ordinal);
            Assert.Equal("question", request.Prompt);
        }
        else
        {
            Assert.Empty(request.Images);
            Assert.DoesNotContain(EncodedImage, request.PreparedBodyJson!, StringComparison.Ordinal);
            Assert.Contains("image contents are unavailable", request.Prompt, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Plain_text_turns_stay_unchanged()
    {
        using var http = new HttpClient();
        var provider = new CapturingProvider("chat_completions", http);
        var service = new ModelStepService(provider);
        await service.RunAsync(new ModelGenerationRequest("hello", [], SystemPromptContext.Empty,
            new ModelGenerationSettings(), Bootstrap("chat_completions", "text-model")), default);

        Assert.Equal("hello", provider.Received!.Prompt);
        Assert.Empty(provider.Received.Images);
    }

    private static ProviderBootstrapData Bootstrap(string protocol, string model, bool? accepts = null) => new(
        "fixture", protocol, new Uri("https://provider.invalid/v1/endpoint"), model, "synthetic",
        maximumOutputTokens: 1024, toolCallingMode: ProviderToolCallingMode.Native,
        allowNativeResponses: true, allowNativeAnthropic: true, contextWindowTokens: 32_768,
        acceptsImageInput: accepts);

    private sealed class CapturingProvider(string protocol, HttpClient http) : IModelProvider, IModelRequestBodyProjector
    {
        public ModelRequest? Received { get; private set; }

        public string ProjectRequestBody(ModelRequest request) => protocol switch
        {
            "chat_completions" => new OpenAiCompatibleModelProvider(http).ProjectRequestBody(request),
            "openai_responses" => new OpenAiResponsesModelProvider(http).ProjectRequestBody(request),
            _ => new AnthropicMessagesModelProvider(http).ProjectRequestBody(request),
        };

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Received = request;
            await Task.Yield();
            yield return new ModelDelta("reply", [], completed: true, finishReason: "stop");
        }
    }
}
