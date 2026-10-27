using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class ActiveContextCacheTests
{
    [Fact]
    public void Large_native_annotations_cannot_hide_behind_small_visible_text_in_cache_weight()
    {
        var messages = new ResponsesAssistantMessages([new JsonObject
        {
            ["type"] = "message", ["id"] = "msg-1", ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "output_text", ["text"] = "ok",
                ["annotations"] = new JsonArray(new string('x', 128 * 1024))
            })
        }]);
        var entry = ModelContextEntry.Assistant("answer", 1,
            new AssistantStep("ok", [], AssistantFinishReason.Stop, 1, responsesMessages: messages));

        Assert.True(ActiveContextCache.Weight([entry]) >= 128 * 1024 * 2);
    }
}
