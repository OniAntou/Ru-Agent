namespace LeaseRegistry;

public sealed class LeaseRegistry
{
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;

    public LeaseRegistry(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public LeaseChangeResult Acquire(string resource, string owner, TimeSpan duration)
    {
        if (!IsValid(resource, owner, duration))
            return new LeaseChangeResult(LeaseChangeStatus.InvalidRequest, null);

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_leases.TryGetValue(resource, out var current) && current.ExpiresAt > now)
                return new LeaseChangeResult(LeaseChangeStatus.AlreadyOwned, current);

            var version = current?.Version + 1 ?? 1;
            var acquired = new Lease(resource, owner, version, now + duration);
            _leases[resource] = acquired;
            return new LeaseChangeResult(LeaseChangeStatus.Applied, acquired);
        }
    }

    public LeaseChangeResult Renew(string resource, string owner, long expectedVersion, TimeSpan duration)
    {
        if (!IsValid(resource, owner, duration))
            return new LeaseChangeResult(LeaseChangeStatus.InvalidRequest, null);

        lock (_gate)
        {
            if (!_leases.TryGetValue(resource, out var current))
                return new LeaseChangeResult(LeaseChangeStatus.NotFound, null);

            var now = _timeProvider.GetUtcNow();
            if (current.ExpiresAt <= now)
                return new LeaseChangeResult(LeaseChangeStatus.Expired, current);
            if (!StringComparer.Ordinal.Equals(current.Owner, owner))
                return new LeaseChangeResult(LeaseChangeStatus.OwnerMismatch, current);
            if (current.Version != expectedVersion)
                return new LeaseChangeResult(LeaseChangeStatus.VersionMismatch, current);

            var renewed = current with { Version = current.Version + 1, ExpiresAt = now + duration };
            _leases[resource] = renewed;
            return new LeaseChangeResult(LeaseChangeStatus.Applied, renewed);
        }
    }

    public LeaseChangeResult Release(string resource, string owner, long expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(owner))
            return new LeaseChangeResult(LeaseChangeStatus.InvalidRequest, null);

        lock (_gate)
        {
            if (!_leases.TryGetValue(resource, out var current))
                return new LeaseChangeResult(LeaseChangeStatus.NotFound, null);

            var now = _timeProvider.GetUtcNow();
            if (current.ExpiresAt <= now)
                return new LeaseChangeResult(LeaseChangeStatus.Expired, current);
            if (!StringComparer.Ordinal.Equals(current.Owner, owner))
                return new LeaseChangeResult(LeaseChangeStatus.OwnerMismatch, current);
            if (current.Version != expectedVersion)
                return new LeaseChangeResult(LeaseChangeStatus.VersionMismatch, current);

            _leases.Remove(resource);
            return new LeaseChangeResult(LeaseChangeStatus.Applied, null);
        }
    }

    public Lease? Get(string resource)
    {
        lock (_gate)
            return _leases.GetValueOrDefault(resource);
    }

    private static bool IsValid(string resource, string owner, TimeSpan duration) =>
        !string.IsNullOrWhiteSpace(resource) && !string.IsNullOrWhiteSpace(owner) && duration > TimeSpan.Zero;
}
