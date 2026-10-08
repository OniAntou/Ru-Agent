namespace LeaseRegistry;

public sealed class LeaseRegistry
{
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public LeaseRegistry(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public LeaseChangeResult Acquire(string resource, string owner, TimeSpan duration)
    {
        if (!IsValid(resource, owner, duration))
            return new LeaseChangeResult(LeaseChangeStatus.InvalidRequest, null);

        var lease = new Lease(resource, owner, 1, _timeProvider.GetUtcNow() + duration);
        _leases[resource] = lease;
        return new LeaseChangeResult(LeaseChangeStatus.Applied, lease);
    }

    public LeaseChangeResult Renew(string resource, string owner, long expectedVersion, TimeSpan duration)
    {
        if (!IsValid(resource, owner, duration))
            return new LeaseChangeResult(LeaseChangeStatus.InvalidRequest, null);
        if (!_leases.TryGetValue(resource, out var current))
            return new LeaseChangeResult(LeaseChangeStatus.NotFound, null);
        if (!StringComparer.Ordinal.Equals(current.Owner, owner))
            return new LeaseChangeResult(LeaseChangeStatus.OwnerMismatch, current);

        var renewed = current with { Version = current.Version + 1, ExpiresAt = _timeProvider.GetUtcNow() + duration };
        _leases[resource] = renewed;
        return new LeaseChangeResult(LeaseChangeStatus.Applied, renewed);
    }

    public LeaseChangeResult Release(string resource, string owner, long expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(owner))
            return new LeaseChangeResult(LeaseChangeStatus.InvalidRequest, null);
        if (!_leases.TryGetValue(resource, out var current))
            return new LeaseChangeResult(LeaseChangeStatus.NotFound, null);
        if (!StringComparer.Ordinal.Equals(current.Owner, owner))
            return new LeaseChangeResult(LeaseChangeStatus.OwnerMismatch, current);

        _leases.Remove(resource);
        return new LeaseChangeResult(LeaseChangeStatus.Applied, null);
    }

    public Lease? Get(string resource) => _leases.GetValueOrDefault(resource);

    private static bool IsValid(string resource, string owner, TimeSpan duration) =>
        !string.IsNullOrWhiteSpace(resource) && !string.IsNullOrWhiteSpace(owner) && duration > TimeSpan.Zero;
}
