namespace RuAgent.Broker.Runtime;

/// <summary>
/// A lease generation's monotonic revocation, shared by policy rebinds. Scopes
/// retain this small proof after close; run IDs alone cannot revive authority.
/// </summary>
internal sealed class BrokerRunLifetime
{
    private int _revoked;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool IsRevoked => Volatile.Read(ref _revoked) != 0;
    public void Revoke() => Interlocked.Exchange(ref _revoked, 1);
}
