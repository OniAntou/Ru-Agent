namespace RuAgent.Protocol.Harness;

/// <summary>
/// How long an approval request waits for its answer, shared by the Host's durable approval and
/// the Broker's pending approval so the two can never disagree. A run parked on an approval
/// stays parked this long before it fails as approval_expired; the answer is still re-evaluated
/// against the run's current policy snapshot when it arrives, and the authorization it yields
/// stays a single-use token of about a minute.
/// </summary>
public static class ApprovalLimits
{
    /// <summary>
    /// A day, so an unattended long run that stops on a permission request waits for the user to
    /// come back instead of failing after the half hour the lifetime used to be.
    /// </summary>
    public const int PendingApprovalLifetimeMinutes = 24 * 60;

    public static TimeSpan PendingApprovalLifetime { get; } = TimeSpan.FromMinutes(PendingApprovalLifetimeMinutes);
}
