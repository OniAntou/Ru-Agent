using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class NativeToolGuidanceRegressionTests
{
    [Theory]
    [InlineData("chat", true)]
    [InlineData("responses", true)]
    [InlineData("anthropic", true)]
    [InlineData("chat", false)]
    [InlineData("responses", false)]
    [InlineData("anthropic", false)]
    public void Projection_preserves_guidance_only_in_native_mode(string protocol, bool native)
    {
        var definition = AgentToolDefinitionCatalog.Get().Single(d => d.ToolId == "read");
        var settings = new JsonObject { ["model_tools"] = new JsonArray(new JsonObject
        {
            ["tool_id"] = definition.ToolId,
            ["description"] = definition.Description,
            ["input_schema"] = definition.InputSchema,
        }) };
        using var http = new HttpClient();
        IModelRequestBodyProjector projector = protocol switch
        {
            "chat" => new OpenAiCompatibleModelProvider(http),
            "responses" => new OpenAiResponsesModelProvider(http),
            _ => new AnthropicMessagesModelProvider(http),
        };
        var request = new ModelRequest("test-model", "system", "hello", [], settings,
            new Uri("https://provider.invalid/v1"), "synthetic", 1024, nativeToolCalling: native);
        var body = JsonNode.Parse(projector.ProjectRequestBody(request))!;
        if (!native)
        {
            Assert.Null(body["tools"]);
            return;
        }
        var declaration = Assert.Single(body["tools"]!.AsArray())!;
        Assert.Equal(definition.Description, (declaration["function"] ?? declaration)["description"]!.GetValue<string>());
    }
}
