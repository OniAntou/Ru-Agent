namespace RuAgent.Broker.Runtime;

/// <summary>
/// A lease generation's monotonic revocation, shared by policy rebinds. Scopes
/// retain this small proof after close; run IDs alone cannot revive authority.
/// </summary>
internal sealed class BrokerRunLifetime
{
    private readonly CancellationTokenSource _revocation = new();
    private int _revoked;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool IsRevoked => Volatile.Read(ref _revoked) != 0;
    public CancellationToken RevocationToken => _revocation.Token;

    public void Revoke()
    {
        if (Interlocked.Exchange(ref _revoked, 1) != 0)
        {
            return;
        }

        try
        {
            _revocation.Cancel();
        }
        catch (AggregateException)
        {
            // All registered operations are signalled even when a callback fails.
        }
    }
}
