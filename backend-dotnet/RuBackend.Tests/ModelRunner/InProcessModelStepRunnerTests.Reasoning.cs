using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Harness.Core;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

/// <summary>
/// The model runner's reasoning stream: it forwards provider reasoning as its own delta
/// kind with its own sequence, and a retried attempt clears the line the failed one left.
/// </summary>
public sealed partial class InProcessModelStepRunnerTests
{
    [Fact]
    public async Task Reasoning_deltas_are_published_with_their_own_kind_and_sequence()
    {
        var provider = new ScriptedProvider((_, _) => Deltas(
            new ModelDelta(null, [], completed: false, finishReason: null, reasoning: "think "),
            new ModelDelta(null, [], completed: false, finishReason: null, reasoning: "more"),
            new ModelDelta("done", [], completed: true, finishReason: "stop")));
        var deltas = new List<AssistantDelta>();

        await Runner(provider).RunStepAsync(
            Request(),
            delta =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        var reasoning = deltas.Where(delta => delta.Kind == AssistantDeltaKind.Reasoning).ToArray();
        Assert.Equal(["think ", "more"], reasoning.Select(delta => delta.Content));
        Assert.Equal([1L, 2L], reasoning.Select(delta => delta.Sequence));

        var text = deltas.Where(delta => delta.Kind == AssistantDeltaKind.Text).ToArray();
        Assert.Equal(["done"], text.Select(delta => delta.Content));
        Assert.Equal([1L], text.Select(delta => delta.Sequence));
    }

    [Fact]
    public async Task A_retry_that_already_streamed_reasoning_publishes_a_reasoning_reset()
    {
        var provider = new ScriptedProvider((_, attempt) => attempt == 1
            ? DeltasThenThrows(
                new ModelDelta(null, [], completed: false, finishReason: null, reasoning: "old thinking"),
                new ModelProviderException("rate_limited", retryable: true))
            : Deltas(
                new ModelDelta(null, [], completed: false, finishReason: null, reasoning: "new thinking"),
                new ModelDelta("answer", [], completed: true, finishReason: "stop")));
        var deltas = new List<AssistantDelta>();

        var result = await Runner(provider).RunStepAsync(
            Request(),
            delta =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("answer", result.Step.Text);
        Assert.Equal(2, provider.Calls);

        var reasoning = deltas.Where(delta => delta.Kind == AssistantDeltaKind.Reasoning).ToArray();
        Assert.Equal("old thinking", reasoning[0].Content);
        Assert.True(reasoning[1].IsReset);
        Assert.Equal(string.Empty, reasoning[1].Content);
        Assert.Equal("new thinking", reasoning[2].Content);
        Assert.Equal(
            reasoning.Select(delta => delta.Sequence).OrderBy(value => value).ToArray(),
            reasoning.Select(delta => delta.Sequence).ToArray());
    }
}
