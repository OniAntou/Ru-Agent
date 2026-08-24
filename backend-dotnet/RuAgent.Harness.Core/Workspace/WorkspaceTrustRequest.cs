using RuAgent.Harness.Core;

namespace RuAgent.Harness.Core.Workspace;

public sealed record WorkspaceTrustRequest
{
    public WorkspaceTrustRequest(
        TrustRequestId requestId,
        WorkspaceIdentity workspace,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
        : this(requestId, workspace, createdAt, expiresAt, null)
    {
    }

    private WorkspaceTrustRequest(
        TrustRequestId requestId,
        WorkspaceIdentity workspace,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        WorkspaceTrustDecision? appliedDecision)
    {
        _ = requestId.Value;
        ArgumentNullException.ThrowIfNull(workspace);
        if (expiresAt <= createdAt)
        {
            throw new ArgumentException(
                "Trust request expiry must be later than its creation time.",
                nameof(expiresAt));
        }

        RequestId = requestId;
        Workspace = workspace;
        DisplayedIdentityHash = workspace.DisplayIdentityHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        AppliedDecision = appliedDecision;
    }

    public TrustRequestId RequestId { get; }

    public WorkspaceIdentity Workspace { get; }

    public string DisplayedIdentityHash { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public WorkspaceTrustDecision? AppliedDecision { get; }

    public WorkspaceTrustApplyResult TryApply(
        WorkspaceTrustResponse response,
        WorkspaceIdentity currentIdentity,
        DateTimeOffset decidedAt)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(currentIdentity);

        if (response.RequestId != RequestId)
        {
            return Reject("trust_request_mismatch");
        }

        if (response.Workspace != Workspace || currentIdentity != Workspace)
        {
            return Reject("workspace_identity_mismatch");
        }

        if (!string.Equals(
                response.DisplayedIdentityHash,
                DisplayedIdentityHash,
                StringComparison.Ordinal))
        {
            return Reject("trust_display_hash_mismatch");
        }

        if (!Enum.IsDefined(response.Decision))
        {
            return Reject("invalid_trust_decision");
        }

        if (AppliedDecision is { } existing)
        {
            return existing == response.Decision
                ? WorkspaceTrustApplyResult.Accept(this, idempotent: true)
                : Reject("trust_decision_conflict");
        }

        if (decidedAt < CreatedAt || decidedAt > ExpiresAt)
        {
            return Reject("trust_request_expired");
        }

        var next = new WorkspaceTrustRequest(
            RequestId,
            Workspace,
            CreatedAt,
            ExpiresAt,
            response.Decision);
        return WorkspaceTrustApplyResult.Accept(next, idempotent: false);
    }

    private WorkspaceTrustApplyResult Reject(string errorCode) =>
        WorkspaceTrustApplyResult.Reject(this, errorCode);
}

public readonly record struct WorkspaceTrustApplyResult(
    bool Accepted,
    bool Idempotent,
    WorkspaceTrustRequest Next,
    string? ErrorCode)
{
    public static WorkspaceTrustApplyResult Accept(
        WorkspaceTrustRequest next,
        bool idempotent) =>
        new(true, idempotent, next, null);

    public static WorkspaceTrustApplyResult Reject(
        WorkspaceTrustRequest current,
        string errorCode) =>
        new(false, false, current, errorCode);
}
