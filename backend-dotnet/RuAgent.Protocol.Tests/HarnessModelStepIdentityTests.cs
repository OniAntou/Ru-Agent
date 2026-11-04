using System.Text.Json;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class HarnessModelStepIdentityTests
{
    [Fact]
    public void Optional_step_identity_roundtrips_and_legacy_envelopes_remain_readable()
    {
        const string legacy = """{"protocol_version":1,"event_id":"evt-1","run_id":"run-1","session_id":"session-1","sequence":1,"event_type":"model.step_started","occurred_at":"2026-11-02","status":"running","safe_summary":"started"}""";
        var envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(legacy)!;
        Assert.Null(envelope.ModelStepId);
        var json = JsonSerializer.Serialize(envelope with { ModelStepId = "step-1" });
        Assert.False(HarnessEventContractValidator.ContainsForbiddenField(json));
        Assert.Equal("step-1", JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(json)!.ModelStepId);
        Assert.Equal(1, envelope.ProtocolVersion);
    }
}
