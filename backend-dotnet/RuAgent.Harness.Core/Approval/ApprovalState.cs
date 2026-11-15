namespace RuAgent.Harness.Core.Approval;

/// <summary>
/// The lifetime of a durable approval request. Resolution itself is owned by the Host
/// approval service and the Broker's single-use approval tokens.
/// </summary>
public static class ApprovalState
{
    public const int DefaultExpiryMinutes = RuAgent.Protocol.Harness.ApprovalLimits.PendingApprovalLifetimeMinutes;

    /// <summary>
    /// The lifetime a durable approval is bound to. A resolution that arrives after
    /// this window is refused, so a run still waiting past it can only be released
    /// by cancelling or failing it.
    /// </summary>
    public static readonly TimeSpan DefaultLifetime =
        TimeSpan.FromMinutes(DefaultExpiryMinutes);

    /// <summary>
    /// True once the approval requested at <paramref name="requestedAt"/> can no
    /// longer be resolved by any client. Callers use this to fail an abandoned
    /// waiting run instead of leaving it parked with its active-session ownership.
    /// </summary>
    public static bool IsExpired(DateTimeOffset requestedAt, DateTimeOffset now) =>
        now - requestedAt > DefaultLifetime;
}
