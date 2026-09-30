using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class HarnessEventContractAllowlistTests
{
    [Fact]
    public void Event_contract_allowlist_covers_every_durable_kind()
    {
        foreach (var kind in new[]
        {
            "reconciliation.resolved",
            "context.compacted",
            "model.usage_recorded",
            "model.reasoning_recorded",
        })
        {
            Assert.True(HarnessEventContractValidator.IsAllowedEventType(kind));
        }

        // Host-internal prompt receipts advance the SSE cursor without mapping.
        Assert.False(HarnessEventContractValidator.IsAllowedEventType("model.prompt_composed"));
    }
}
