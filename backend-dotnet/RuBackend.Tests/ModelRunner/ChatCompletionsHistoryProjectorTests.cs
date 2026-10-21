using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;
using RuBackend.Harness.ModelRunner;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

public sealed class ChatCompletionsHistoryProjectorTests
{
    [Fact]
    public void Rehydrated_private_reasoning_matches_model_tool_arguments_and_text()
    {
        var arguments = JsonDocument.Parse("{\"path\":\"file.txt\"}").RootElement.Clone();
        var continuation = ChatProviderContinuation.Create("fixture", "working", "complete reasoning",
            [new ModelToolCall("read_1", "read", "1.0.0", arguments)]);
        var turn = new ModelTurn("assistant", "working", [new ModelFunctionCall("read_1", "read", arguments)]);
        var restored = Assert.Single(ChatCompletionsHistoryProjector.Project([turn], [continuation], "fixture"));
        Assert.Equal("complete reasoning", restored.ReasoningContent);
        Assert.Throws<ModelRunnerProtocolException>(() => ChatCompletionsHistoryProjector.Project([turn], [continuation], "other"));
        Assert.Throws<ModelRunnerProtocolException>(() => ChatCompletionsHistoryProjector.Project([turn], [], "fixture"));
        var changed = new ModelTurn("assistant", "working", [new ModelFunctionCall("read_1", "read",
            JsonDocument.Parse("{\"path\":\"other.txt\"}").RootElement)]);
        Assert.Throws<ModelRunnerProtocolException>(() => ChatCompletionsHistoryProjector.Project([changed], [continuation], "fixture"));
    }
}
