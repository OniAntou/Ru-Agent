using RuAgent.Protocol.Harness;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentRunModelTests
{
    [Fact]
    public void FromCreateResponse_maps_fields()
    {
        var response = new CreateAgentRunResponse(
            "run_12345678901234567890123456789012",
            "sess_1",
            "ws_1",
            "running",
            "standard",
            "2026-01-01",
            "2026-01-01");
        var model = AgentRunModel.FromCreateResponse(response);

        Assert.Equal("run_12345678901234567890123456789012", model.RunId);
        Assert.Equal("sess_1", model.SessionId);
        Assert.Equal("ws_1", model.WorkspaceId);
        Assert.Equal("running", model.Status);
        Assert.Equal("standard", model.Preset);
        Assert.Equal("2026-01-01", model.CreatedAt);
        Assert.Equal("2026-01-01", model.UpdatedAt);
    }

    [Fact]
    public void FromGetResponse_maps_all_fields()
    {
        var response = new GetAgentRunResponse(
            "run_12345678901234567890123456789012",
            "sess_1",
            "ws_1",
            "terminal",
            "waiting_user",
            "standard",
            7,
            "sha256",
            3,
            42,
            "2026-01-01",
            "2026-01-01",
            "2026-01-01",
            "cancelled_side_effects_possible",
            "trust_1",
            "hash_abc");
        var model = AgentRunModel.FromGetResponse(response);

        Assert.Equal(7, model.PolicySnapshotVersion);
        Assert.Equal("sha256", model.PolicySnapshotSha256);
        Assert.Equal(3, model.Version);
        Assert.Equal(42, model.LastSequence);
        Assert.Equal("waiting_user", model.HistoryWaitKind);
        Assert.Equal("trust_1", model.TrustRequestId);
        Assert.Equal("hash_abc", model.DisplayIdentityHash);
        Assert.Equal("cancelled_side_effects_possible", model.TerminalErrorCode);
    }
}
