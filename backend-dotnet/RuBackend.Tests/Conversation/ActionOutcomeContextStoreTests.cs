using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RuBackend.Conversation;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Conversation;

public sealed class ActionOutcomeContextStoreTests
{
    [Fact]
    public void Put_replaces_and_take_is_one_shot_per_session()
    {
        IActionOutcomeContextStore store = new ActionOutcomeContextStore();
        store.Put("session-1", Failed("write_failed"));
        store.Put("session-1", Failed("readback_mismatch"));
        store.Put("session-2", Failed("read_failed"));

        Assert.Equal("readback_mismatch", store.Take("session-1")!.ReasonCode);
        Assert.Null(store.Take("session-1"));
        Assert.Equal("read_failed", store.Take("session-2")!.ReasonCode);
    }

    [Fact]
    public void Remove_clears_only_the_matching_session()
    {
        IActionOutcomeContextStore store = new ActionOutcomeContextStore();
        store.Put("session-1", Failed("write_failed"));
        store.Put("session-2", Failed("read_failed"));

        store.Remove("session-1");

        Assert.Null(store.Take("session-1"));
        Assert.Equal("read_failed", store.Take("session-2")!.ReasonCode);
    }

    [Fact]
    public void Successful_outcome_removes_a_previous_failure()
    {
        IActionOutcomeContextStore store = new ActionOutcomeContextStore();
        store.Put("session-1", Failed("write_failed"));

        store.Put("session-1", Succeeded());

        Assert.Null(store.Take("session-1"));
    }

    [Fact]
    public async Task Concurrent_take_returns_the_outcome_to_exactly_one_caller()
    {
        IActionOutcomeContextStore store = new ActionOutcomeContextStore();
        store.Put("session-1", Failed("write_failed"));
        var results = new ConcurrentBag<WorkerActionOutcome>();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            if (store.Take("session-1") is { } outcome)
            {
                results.Add(outcome);
            }
        })));

        Assert.Equal("write_failed", Assert.Single(results).ReasonCode);
    }

    private static readonly WorkerAdvertisedTools AdvertisedVolume =
        new(new HashSet<(string ToolId, string ToolVersion)> { ("system.volume", "1.0.0") });

    private static WorkerActionOutcome Failed(string reasonCode) =>
        WorkerActionOutcome.Parse(new JsonObject
        {
            ["schema_version"] = 1,
            ["tool_id"] = "system.volume",
            ["tool_version"] = "1.0.0",
            ["status"] = "failed",
            ["reason_code"] = reasonCode,
        }, AdvertisedVolume);

    private static WorkerActionOutcome Succeeded() =>
        WorkerActionOutcome.Parse(new JsonObject
        {
            ["schema_version"] = 1,
            ["tool_id"] = "system.volume",
            ["tool_version"] = "1.0.0",
            ["status"] = "succeeded",
            ["reason_code"] = null,
        }, AdvertisedVolume);
}
