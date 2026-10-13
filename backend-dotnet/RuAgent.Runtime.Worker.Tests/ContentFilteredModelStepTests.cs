using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using Xunit;

namespace RuAgent.Runtime.Model.Tests;

public sealed class ContentFilteredModelStepTests
{
    [Fact]
    public async Task Content_filter_finish_discards_untrusted_native_calls_before_parsing()
    {
        var request = new ModelGenerationRequest("inspect the file", [], SystemPromptContext.Empty,
            new ModelGenerationSettings(new JsonArray(new JsonObject
            {
                ["tool_id"] = "files.read", ["tool_version"] = "1.0.0",
                ["scopes"] = new JsonArray("workspace.read"), ["availability"] = "executable",
                ["model_enabled"] = true, ["capability_class"] = "tool", ["verification_required"] = false,
            })),
            new ProviderBootstrapData("fixture", ProviderProtocols.ChatCompletions,
                new Uri("https://provider.invalid/v1/chat/completions"), "fixture-model", "synthetic"));
        var provider = new Provider(new ModelDelta("filtered text",
            [new ModelToolFragment("call-1", "files.read", "{\"path\":")], true, "content_filter"));

        var result = await new ModelStepService(provider).RunAsync(request, default);

        Assert.Equal("content_filter", result.FinishReason);
        Assert.Empty(result.ToolCalls);
    }

    private sealed class Provider(ModelDelta delta) : IModelProvider
    {
        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return delta;
        }
    }
}
