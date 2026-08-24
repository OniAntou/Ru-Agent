using RuAgent.Harness.Core.Workspace;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class WorkspaceTrustStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Decision_is_bound_to_request_displayed_hash_and_current_identity()
    {
        var identity = Identity("ws_11111111111111111111111111111111", "root-a", 'a');
        var request = Request(identity);
        var approve = Response(request, identity, WorkspaceTrustDecision.Approve);

        AssertRejected(
            request,
            approve with { RequestId = new TrustRequestId("trust_22222222222222222222222222222222") },
            identity,
            Now,
            "trust_request_mismatch");
        AssertRejected(
            request,
            approve with { DisplayedIdentityHash = new string('b', 64) },
            identity,
            Now,
            "trust_display_hash_mismatch");
        AssertRejected(
            request,
            approve,
            Identity(identity.WorkspaceId.Value, "root-moved", 'c'),
            Now,
            "workspace_identity_mismatch");
        AssertRejected(
            request,
            approve,
            identity,
            request.ExpiresAt.AddTicks(1),
            "trust_request_expired");
    }

    [Fact]
    public void Exact_repeated_decision_is_idempotent_but_conflicting_replay_is_rejected()
    {
        var identity = Identity("ws_11111111111111111111111111111111", "root-a", 'a');
        var request = Request(identity);
        var approve = Response(request, identity, WorkspaceTrustDecision.Approve);

        var first = request.TryApply(approve, identity, Now);
        var replay = first.Next.TryApply(approve, identity, request.ExpiresAt.AddHours(1));
        var conflict = first.Next.TryApply(
            approve with { Decision = WorkspaceTrustDecision.Deny },
            identity,
            Now);

        Assert.True(first.Accepted);
        Assert.False(first.Idempotent);
        Assert.Equal(WorkspaceTrustDecision.Approve, first.Next.AppliedDecision);
        Assert.True(replay.Accepted);
        Assert.True(replay.Idempotent);
        Assert.Equal(first.Next, replay.Next);
        Assert.False(conflict.Accepted);
        Assert.Equal("trust_decision_conflict", conflict.ErrorCode);
        Assert.Equal(first.Next, conflict.Next);
    }

    [Fact]
    public void Workspace_identity_inside_the_response_must_match_the_request()
    {
        var identity = Identity("ws_11111111111111111111111111111111", "root-a", 'a');
        var moved = Identity(identity.WorkspaceId.Value, "root-moved", 'c');
        var request = Request(identity);
        var response = Response(request, moved, WorkspaceTrustDecision.Approve);

        AssertRejected(
            request,
            response,
            identity,
            Now,
            "workspace_identity_mismatch");
    }

    [Fact]
    public void Unknown_trust_decision_fails_closed()
    {
        var identity = Identity("ws_11111111111111111111111111111111", "root-a", 'a');
        var request = Request(identity);
        var response = Response(
            request,
            identity,
            (WorkspaceTrustDecision)int.MaxValue);

        AssertRejected(
            request,
            response,
            identity,
            Now,
            "invalid_trust_decision");
    }

    [Fact]
    public void Denial_cancels_the_run_before_it_can_enter_running()
    {
        var waiting = AgentRunStateMachine.TryTransition(
            new HarnessRunStatus(HarnessRunState.Created),
            HarnessRunTransition.BeginWorkspaceTrust);
        var denied = AgentRunStateMachine.TryTransition(
            waiting.Next,
            HarnessRunTransition.DenyWorkspaceTrust);
        var attemptedModelStep = AgentRunStateMachine.TryTransition(
            denied.Next,
            HarnessRunTransition.AcknowledgeUserHistory);

        Assert.True(waiting.Accepted);
        Assert.True(denied.Accepted);
        Assert.Equal(HarnessRunState.Cancelled, denied.Next.State);
        Assert.False(attemptedModelStep.Accepted);
        Assert.Equal("run_terminal", attemptedModelStep.ErrorCode);
    }

    [Fact]
    public void Default_identifiers_are_rejected_at_workspace_trust_boundaries()
    {
        Assert.Throws<InvalidOperationException>(() => new WorkspaceIdentity(
            default,
            "A1B2-C3D4",
            "root-a",
            new string('a', 64)));

        var identity = Identity("ws_11111111111111111111111111111111", "root-a", 'a');
        Assert.Throws<InvalidOperationException>(() => new WorkspaceTrustRequest(
            default,
            identity,
            Now,
            Now.AddMinutes(5)));
    }

    private static WorkspaceTrustRequest Request(WorkspaceIdentity identity) =>
        new(
            new TrustRequestId("trust_11111111111111111111111111111111"),
            identity,
            Now,
            Now.AddMinutes(5));

    private static WorkspaceTrustResponse Response(
        WorkspaceTrustRequest request,
        WorkspaceIdentity identity,
        WorkspaceTrustDecision decision) =>
        new(
            request.RequestId,
            identity,
            request.DisplayedIdentityHash,
            decision);

    private static WorkspaceIdentity Identity(string workspaceId, string rootFileId, char hash) =>
        new(
            new WorkspaceId(workspaceId),
            "A1B2-C3D4",
            rootFileId,
            new string(hash, 64));

    private static void AssertRejected(
        WorkspaceTrustRequest request,
        WorkspaceTrustResponse response,
        WorkspaceIdentity currentIdentity,
        DateTimeOffset decidedAt,
        string errorCode)
    {
        var result = request.TryApply(response, currentIdentity, decidedAt);

        Assert.False(result.Accepted);
        Assert.False(result.Idempotent);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal(request, result.Next);
    }
}
