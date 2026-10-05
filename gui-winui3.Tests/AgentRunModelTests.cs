using RuAgent.Protocol.Harness;
using RuAgent.Client;
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

    [Fact]
    public void Handoff_is_explicitly_source_dated_partial_and_workspace_aware()
    {
        var source = CreateWorkStateRun();

        var attached = WorkStateHandoffContent.Create(source, "ws_other");

        Assert.NotNull(attached);
        Assert.Contains("Latest state source run: run_source", attached);
        Assert.Contains("Selected run: run_selected", attached);
        Assert.Contains("Last updated: 2026-10-04T12:00:00Z", attached);
        Assert.Contains("different workspace", attached);
        Assert.Contains("partial", attached);
        Assert.Contains("unfinished task", attached);
        Assert.Contains("old hashes are historical", attached);
    }

    [Fact]
    public void Handoff_refuses_missing_state_and_oversized_preview()
    {
        var source = CreateWorkStateRun();

        Assert.Null(WorkStateHandoffContent.Create(source with { WorkStateStatus = "unavailable" }, "ws_1"));
        Assert.Null(WorkStateHandoffContent.Create(source with { WorkStatePreview = new string('x', 20_000) }, "ws_1"));
        Assert.Contains("target workspace identity is unknown",
            WorkStateHandoffContent.Create(source, null)!);
    }

    [Fact]
    public void Handoff_validation_binds_selection_and_current_draft_owner()
    {
        var source = CreateWorkStateRun();
        var target = new WorkStateHandoffTarget("sess_target", "ws_target", 3, 8, "draft", ["attachment-1"]);

        Assert.False(WorkStateHandoffCoordinator.Prepare(source, "run_other", "sess_1", "ws_target").IsReady);
        Assert.False(WorkStateHandoffCoordinator.Prepare(source, "run_selected", "sess_other", "ws_target").IsReady);
        Assert.True(WorkStateHandoffCoordinator.Prepare(source, "run_selected", "sess_1", "ws_target").IsReady);
        Assert.True(WorkStateHandoffCoordinator.IsCurrent(
            target, "sess_target", "ws_target", 3, 8, "draft", ["attachment-1"]));
        Assert.False(WorkStateHandoffCoordinator.IsCurrent(
            target, "sess_changed", "ws_target", 3, 8, "draft", ["attachment-1"]));
        Assert.False(WorkStateHandoffCoordinator.IsCurrent(
            target, "sess_target", "ws_target", 3, 8, "edited draft", ["attachment-1"]));
        Assert.False(WorkStateHandoffCoordinator.IsCurrent(
            target, "sess_target", "ws_target", 3, 8, "draft", []));
    }

    [Fact]
    public async Task Delayed_handoff_read_cannot_transfer_context_after_target_changes()
    {
        var pendingRead = new TaskCompletionSource<AgentRunModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool isTargetCurrent = true;
        var preparationTask = WorkStateHandoffCoordinator.PrepareSelectedRunAsync(
            "run_selected", "sess_1", "ws_target",
            (_, _) => pendingRead.Task,
            () => isTargetCurrent,
            CancellationToken.None);

        isTargetCurrent = false;
        pendingRead.SetResult(CreateWorkStateRun());
        var preparation = await preparationTask;

        Assert.False(preparation.IsReady);
        Assert.Null(preparation.Content);
        Assert.Contains("target chat changed", preparation.Error);
    }

    [Fact]
    public async Task Failed_handoff_read_returns_named_unavailable_result()
    {
        var preparation = await WorkStateHandoffCoordinator.PrepareSelectedRunAsync(
            "run_selected", "sess_1", "ws_target",
            (_, _) => throw new InvalidOperationException("private response detail"),
            () => true,
            CancellationToken.None);

        Assert.False(preparation.IsReady);
        Assert.Equal("The selected saved run could not be read.", preparation.Error);
        Assert.DoesNotContain("private response detail", preparation.Error);
    }

    private static AgentRunModel CreateWorkStateRun() => AgentRunModel.FromGetResponse(new GetAgentRunResponse(
        "run_selected", "sess_1", "ws_1", "succeeded", null, "standard", 1, "sha", 1, 5,
        "2026-10-04", "2026-10-04", "2026-10-04", null, null, "hash",
        WorkStateStatus: "available", WorkStatePartial: true, WorkStateIntentOmitted: true,
        WorkStatePreviewOmitted: true, WorkStateSourceRunId: "run_source",
        WorkStateSourceEventId: "evt_7", WorkStateUpdatedAt: "2026-10-04T12:00:00Z",
        WorkStatePreview: "unfinished task; stale file hash"));
}
