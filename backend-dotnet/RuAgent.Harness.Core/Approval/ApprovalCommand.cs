namespace RuAgent.Harness.Core.Approval;

/// <summary>
/// The typed user decision for one pending tool approval.
/// </summary>
public enum ApprovalCommandKind
{
    AllowOnce,
    Deny,
}

public readonly record struct ApprovalCommand(ApprovalCommandKind Kind)
{
    public static ApprovalCommand AllowOnce => new(ApprovalCommandKind.AllowOnce);
    public static ApprovalCommand Deny => new(ApprovalCommandKind.Deny);

    public bool IsAllow => Kind is ApprovalCommandKind.AllowOnce;
}
