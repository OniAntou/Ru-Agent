using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class WorkerV2PromptContractTests
{
    [Fact]
    public void Start_requires_typed_system_prompt_context()
    {
        var payload = new JsonObject
        {
            ["prompt"] = "đọc README",
            ["turns"] = new JsonArray(),
            ["settings"] = new JsonObject { ["model_tools"] = new JsonArray() },
        };

        var exception = Assert.Throws<AgentProtocolException>(() =>
            WorkerV2Contracts.ValidateStart(payload));

        Assert.Equal("invalid_request", exception.Code);
    }

    [Fact]
    public void Done_requires_prompt_receipt()
    {
        var payload = new JsonObject
        {
            ["text"] = "x",
            ["tool_calls"] = new JsonArray(),
            ["finish_reason"] = "stop",
        };

        var exception = Assert.Throws<AgentProtocolException>(() =>
            WorkerV2Contracts.ValidateStepDone(payload));

        Assert.Equal("invalid_step", exception.Code);
    }
}
