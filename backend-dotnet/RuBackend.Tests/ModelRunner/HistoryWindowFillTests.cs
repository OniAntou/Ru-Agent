using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using RuBackend.Harness.ModelRunner;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

/// <summary>
/// The history-window fill decides when a long run compacts before the runner drops history.
/// A native Anthropic or Responses history carries the replayed reasoning the portable
/// messages do not, so the fill must measure the history the request actually sends.
/// </summary>
public sealed class HistoryWindowFillTests
{
    [Fact]
    public void A_native_history_counts_toward_the_window_by_its_own_bytes()
    {
        // Two portable messages, but a native history half the size of the byte window.
        var half = ModelGenerationBounds.MaxModelMessagesBytes / 2;
        var anthropic = new[]
        {
            new JsonObject { ["role"] = "user", ["content"] = "start" },
            new JsonObject { ["role"] = "assistant", ["content"] = new string('r', half) },
        };

        var fill = InProcessModelStepRunner.HistoryWindowFill(Request(anthropic));

        Assert.InRange(fill, 0.49, 0.51);
    }

    [Fact]
    public void A_short_portable_history_leaves_the_window_nearly_empty()
    {
        var fill = InProcessModelStepRunner.HistoryWindowFill(Request(anthropic: null));

        Assert.InRange(fill, 0, 0.01);
    }

    private static ModelGenerationRequest Request(IReadOnlyList<JsonObject>? anthropic)
    {
        var context = new SystemPromptContext(2, "ru-agent", "standard",
            new PromptEnvironment("fixture", "windows", "vi-VN", "UTC", "2026-11-12", "fixture"), [], [],
            new PromptRuntimeContext([]));
        return new ModelGenerationRequest(
            "continue",
            [new ModelTurn("user", "start"), new ModelTurn("assistant", "ok")],
            context,
            new ModelGenerationSettings(new JsonArray()),
            new ProviderBootstrapData("fixture", "anthropic_messages",
                new Uri("https://provider.invalid/v1/messages"), "fixture", "synthetic", maximumOutputTokens: 4096),
            anthropicMessages: anthropic);
    }
}
