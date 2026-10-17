using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using Xunit;

namespace RuAgent.Runtime.Model.Tests;

public sealed class TruncatedModelStepTests
{
    [Theory]
    [InlineData("{\"path\":\"README.md\"}")]
    [InlineData("{\"path\":")]
    public async Task Length_finish_discards_native_calls_before_parsing(string arguments)
    {
        var result = await RunAsync(new ModelDelta("unfinished reply",
            [new ModelToolFragment("call-1", "files.read", arguments)], true, "length"));
        Assert.Equal("length", result.FinishReason);
        Assert.Empty(result.ToolCalls);
    }

    [Fact]
    public async Task Length_finish_does_not_promote_text_selection_into_a_tool_call()
    {
        var result = await RunAsync(new ModelDelta(
            "<tool_call>{\"tool_id\":\"files.read\",\"tool_version\":\"1.0.0\",\"arguments\":{\"path\":\"README.md\"}}</tool_call>",
            [], true, "length"));
        Assert.Equal("length", result.FinishReason);
        Assert.Empty(result.ToolCalls);
    }

    private static Task<ModelStepServiceResult> RunAsync(ModelDelta delta)
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
        return new ModelStepService(new Provider(delta)).RunAsync(request, default);
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
