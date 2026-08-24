using RuAgent.Harness.Core;

namespace RuAgent.Harness.Core.Workspace;

public enum WorkspaceTrustDecision
{
    Approve,
    Deny,
}

public sealed record WorkspaceTrustResponse(
    TrustRequestId RequestId,
    WorkspaceIdentity Workspace,
    string DisplayedIdentityHash,
    WorkspaceTrustDecision Decision);
