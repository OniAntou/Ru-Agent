namespace RuBackend.Harness.Broker;

/// <summary>
/// The Broker's non-consuming answer about one pending approval. <see cref="Known"/> is false when the authority
/// does not hold the approval as pending for the run (unknown, foreign, unleased or expired).
/// <see cref="Rememberable"/> says whether allow_always may remember its tool class and only means something when
/// <see cref="Known"/> is true.
/// </summary>
public sealed record BrokerApprovalProbe(bool Known, bool Rememberable)
{
    public static BrokerApprovalProbe Unknown { get; } = new(false, false);
}
