using System.Text.Json;
using LeaseRegistry;

var grades = new List<GradeGroup>
{
    Check("acquire_preserves_live_owner", AcquirePreservesOwner),
    Check("expiry_allows_takeover_and_blocks_old_mutations", ExpiryAllowsTakeover),
    Check("renew_is_versioned_and_extends_from_now", RenewalIsVersioned),
    Check("stale_release_cannot_revoke_renewal", StaleReleaseIsRejected),
    Check("invalid_requests_leave_state_unchanged", InvalidRequestsAreSafe),
    Check("concurrent_mutations_have_one_winner", ConcurrentRenewalsHaveOneWinner),
    Check("reads_do_not_mutate_the_current_lease", ReadsDoNotMutate)
};

Console.WriteLine(JsonSerializer.Serialize(new { groups = grades }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
return grades.All(group => group.Passed) ? 0 : 1;

static GradeGroup Check(string name, Func<IReadOnlyList<string>> test)
{
    try
    {
        var failures = test();
        return new GradeGroup(name, failures.Count == 0, 1, failures.ToArray());
    }
    catch (Exception exception)
    {
        return new GradeGroup(name, false, 1, new[] { exception.GetType().Name + ": " + exception.Message });
    }
}

static IReadOnlyList<string> AcquirePreservesOwner()
{
    var clock = new ManualClock();
    var registry = new LeaseRegistry.LeaseRegistry(clock);
    var first = registry.Acquire("r", "a", TimeSpan.FromMinutes(1));
    var second = registry.Acquire("r", "b", TimeSpan.FromMinutes(1));
    return FailIf(second.Status == LeaseChangeStatus.AlreadyOwned && second.Current == first.Current && registry.Get("r") == first.Current,
        "a competing acquisition must preserve the live lease");
}

static IReadOnlyList<string> ExpiryAllowsTakeover()
{
    var clock = new ManualClock();
    var registry = new LeaseRegistry.LeaseRegistry(clock);
    var first = registry.Acquire("r", "a", TimeSpan.FromSeconds(10)).Current!;
    clock.Advance(TimeSpan.FromSeconds(10));
    var renew = registry.Renew("r", "a", first.Version, TimeSpan.FromSeconds(5));
    var release = registry.Release("r", "a", first.Version);
    var second = registry.Acquire("r", "b", TimeSpan.FromSeconds(5));
    return FailIf(renew.Status == LeaseChangeStatus.Expired && release.Status == LeaseChangeStatus.Expired &&
                  second.Succeeded && second.Current?.Owner == "b" && second.Current.Version > first.Version,
        "expired leases must reject old mutations and permit a newer lease");
}

static IReadOnlyList<string> RenewalIsVersioned()
{
    var clock = new ManualClock();
    var registry = new LeaseRegistry.LeaseRegistry(clock);
    var first = registry.Acquire("r", "a", TimeSpan.FromSeconds(10)).Current!;
    clock.Advance(TimeSpan.FromSeconds(3));
    var renewed = registry.Renew("r", "a", first.Version, TimeSpan.FromSeconds(20));
    var stale = registry.Renew("r", "a", first.Version, TimeSpan.FromSeconds(20));
    return FailIf(renewed.Succeeded && renewed.Current?.Version == first.Version + 1 &&
                  renewed.Current.ExpiresAt == clock.GetUtcNow() + TimeSpan.FromSeconds(20) &&
                  stale.Status == LeaseChangeStatus.VersionMismatch,
        "renewal must compare the expected version and extend from the current time");
}

static IReadOnlyList<string> StaleReleaseIsRejected()
{
    var registry = new LeaseRegistry.LeaseRegistry(new ManualClock());
    var first = registry.Acquire("r", "a", TimeSpan.FromMinutes(1)).Current!;
    var renewed = registry.Renew("r", "a", first.Version, TimeSpan.FromMinutes(2)).Current!;
    var release = registry.Release("r", "a", first.Version);
    return FailIf(release.Status == LeaseChangeStatus.VersionMismatch && registry.Get("r") == renewed,
        "release based on a pre-renewal version must leave the renewed lease intact");
}

static IReadOnlyList<string> InvalidRequestsAreSafe()
{
    var registry = new LeaseRegistry.LeaseRegistry(new ManualClock());
    var before = registry.Acquire("r", "a", TimeSpan.FromMinutes(1)).Current;
    var badAcquire = registry.Acquire("r", "b", TimeSpan.Zero);
    var badRenew = registry.Renew("r", "a", before!.Version, TimeSpan.FromSeconds(-1));
    var badRelease = registry.Release(" ", "a", before.Version);
    return FailIf(badAcquire.Status == LeaseChangeStatus.InvalidRequest && badRenew.Status == LeaseChangeStatus.InvalidRequest &&
                  badRelease.Status == LeaseChangeStatus.InvalidRequest && registry.Get("r") == before,
        "invalid inputs must not change an existing lease");
}

static IReadOnlyList<string> ConcurrentRenewalsHaveOneWinner()
{
    var registry = new LeaseRegistry.LeaseRegistry(new ManualClock());
    var lease = registry.Acquire("r", "a", TimeSpan.FromMinutes(1)).Current!;
    using var start = new ManualResetEventSlim(false);
    var renewals = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
    {
        start.Wait();
        return registry.Renew("r", "a", lease.Version, TimeSpan.FromMinutes(1));
    })).ToArray();
    start.Set();
    Task.WaitAll(renewals);
    return FailIf(renewals.Count(task => task.Result.Succeeded) == 1 &&
                  renewals.Count(task => task.Result.Status == LeaseChangeStatus.VersionMismatch) == 1,
        "exactly one competing renewal with the same version may succeed");
}

static IReadOnlyList<string> ReadsDoNotMutate()
{
    var registry = new LeaseRegistry.LeaseRegistry(new ManualClock());
    var lease = registry.Acquire("r", "a", TimeSpan.FromMinutes(1)).Current!;
    var reads = Enumerable.Range(0, 8).Select(_ => registry.Get("r")).ToArray();
    return FailIf(reads.All(read => read == lease) && registry.Get("r") == lease,
        "reading a lease must preserve its value and version");
}

static IReadOnlyList<string> FailIf(bool passed, string message) => passed ? Array.Empty<string>() : new[] { message };

sealed record GradeGroup(string Name, bool Passed, int Checks, string[] Failures);

sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan amount) => _now += amount;
}
