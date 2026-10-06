using RuAgent.Protocol.Harness;
using RuAgent.Client;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentRunModelTests
{
    [Fact]
    public void Compaction_activity_is_linked_to_its_durable_sequence_without_payload_text()
    {
        var activity = AgentRunEvidenceActivityMapper.Compaction("run_1", 42);

        Assert.Equal("act_compaction_run_1_42", activity.ActivityId);
        Assert.Equal(AgentActivityKind.Compaction, activity.Kind);
        Assert.Equal(AgentActivityState.Completed, activity.State);
        Assert.Equal(42, activity.DurableSequence);
        Assert.Equal("context_compacted", activity.SafeCode);
        Assert.Null(activity.Detail);
        Assert.Null(activity.Todos);
    }

    [Fact]
    public void Live_compaction_projection_identity_matches_the_replay_mapper()
    {
        var live = RuAgent.Harness.Core.AgentActivityProjector.ProjectCompaction(
            "run_1",
            42,
            RuAgent.Harness.Core.AgentActivityState.Completed,
            coveredEntries: 3);
        var mapped = AgentRunEvidenceActivityMapper.Compaction("run_1", 42);

        Assert.Equal(mapped.ActivityId, live.ActivityId);
    }

    [Fact]
    public void Compaction_activity_identity_includes_run_and_sequence_for_replay_live_deduplication()
    {
        var firstReplay = AgentRunEvidenceActivityMapper.Compaction("run_first", 42);
        var secondReplay = AgentRunEvidenceActivityMapper.Compaction("run_second", 42);
        var firstLive = AgentRunEvidenceActivityMapper.Compaction("run_first", 42);

        Assert.NotEqual(firstReplay.ActivityId, secondReplay.ActivityId);
        Assert.Equal(firstReplay.ActivityId, firstLive.ActivityId);
    }

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

    [Fact]
    public void Retained_identity_and_window_are_from_the_run_pin_and_session()
    {
        var run = AgentRunModel.FromGetResponse(new GetAgentRunResponse(
            "run_12345678901234567890123456789012", "sess_1", "ws_1", "running", null,
            "standard", 1, "sha", 1, 0, "2026-10-05", "2026-10-05", null, null, null, "hash",
            ProviderBackend: "openai", ProviderModel: "gpt-5", ProviderProtocol: "openai_responses",
            ProviderModelLabel: "Pinned GPT", ProviderCallingMode: "native", ContextWindowTokens: 80_000));

        Assert.Equal(80_000, RetainedRunPresentation.PublishedContextWindow(run, "sess_1"));
        Assert.Null(RetainedRunPresentation.PublishedContextWindow(run, "sess_2"));
        Assert.Equal("Run pin: Pinned GPT [gpt-5] · openai · openai_responses/native · window 80K (catalog/catalog-v1) · output reserve 8192",
            RetainedRunPresentation.Identity(run with
            {
                ContextWindowSource = "catalog",
                ContextWindowVersion = "catalog-v1",
                OutputTokenReserve = 8192,
            }));
        Assert.Null(RetainedRunPresentation.PublishedContextWindow(run with { ContextWindowTokens = null }, "sess_1"));
    }

}
