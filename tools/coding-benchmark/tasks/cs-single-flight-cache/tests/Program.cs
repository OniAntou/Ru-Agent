using System.Text.Json;
using SingleFlight;

var groups = new List<GroupResult>
{
    ConstructorValidation(), await Run("same_key_flight", SameKeyFlight), await Run("waiter_cancellation", WaiterCancellation),
    await Run("retry_after_failure", RetryAfterFailure), await Run("invalidation_generation", InvalidationGeneration),
    await Run("ttl_expiration", TtlExpiration), await Run("lru_capacity", LruCapacity),
    await Run("distinct_keys_concurrent", DistinctKeysConcurrent)
};
Console.WriteLine(JsonSerializer.Serialize(new { groups }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
return groups.All(group => group.Passed) ? 0 : 1;

static GroupResult ConstructorValidation()
{
    var checks = new Checks();
    var delegateRejected = false; var ttlRejected = false; var capacityRejected = false;
    try { _ = new SingleFlightCache<string, int>(null!, TimeSpan.FromSeconds(1), 1); } catch (ArgumentNullException) { delegateRejected = true; }
    try { _ = new SingleFlightCache<string, int>((_, _) => Task.FromResult(1), TimeSpan.Zero, 1); } catch (ArgumentException) { ttlRejected = true; }
    try { _ = new SingleFlightCache<string, int>((_, _) => Task.FromResult(1), TimeSpan.FromSeconds(1), 0); } catch (ArgumentException) { capacityRejected = true; }
    checks.That(delegateRejected && ttlRejected && capacityRejected, "invalid constructor arguments rejected");
    return new GroupResult("constructor_validation", checks.Failures.Count == 0, checks.Count, checks.Failures.ToArray());
}

static async Task<GroupResult> Run(string name, Func<Checks, Task> test)
{
    var checks = new Checks();
    try { await test(checks); } catch (Exception error) { checks.Fail("uncaught " + error.GetType().Name); }
    return new GroupResult(name, checks.Failures.Count == 0, checks.Count, checks.Failures.Take(8).ToArray());
}

static async Task SameKeyFlight(Checks c)
{
    var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    var cache = new SingleFlightCache<string, int>((_, _) => { Interlocked.Increment(ref calls); started.TrySetResult(); return gate.Task; }, TimeSpan.FromMinutes(1), 4);
    var first = cache.GetAsync("k");
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var second = cache.GetAsync("k");
    c.That(Volatile.Read(ref calls) == 1, "same key starts one loader");
    gate.SetResult(17);
    c.That(await first == 17 && await second == 17, "all waiters receive shared value");
    c.That(await cache.GetAsync("k") == 17 && calls == 1, "successful result is cached");
}

static async Task WaiterCancellation(Checks c)
{
    var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    var cache = new SingleFlightCache<string, string>((_, token) =>
    {
        c.That(token == CancellationToken.None, "loader receives no waiter token");
        Interlocked.Increment(ref calls); started.TrySetResult(); return gate.Task;
    }, TimeSpan.FromMinutes(1), 2);
    using var cancel = new CancellationTokenSource();
    var abandoned = cache.GetAsync("x", cancel.Token);
    await started.Task;
    var survivor = cache.GetAsync("x");
    cancel.Cancel();
    try { await abandoned; c.That(false, "cancelled waiter should stop waiting"); }
    catch (OperationCanceledException) { c.That(true, "one waiter observes cancellation"); }
    gate.SetResult("ok");
    c.That(await survivor == "ok" && calls == 1, "other waiter and shared load survive cancellation");
}

static async Task RetryAfterFailure(Checks c)
{
    var calls = 0;
    var cache = new SingleFlightCache<string, int>((_, _) =>
    {
        if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("first load");
        return Task.FromResult(23);
    }, TimeSpan.FromMinutes(1), 2);
    try { await cache.GetAsync("retry"); c.That(false, "first failure reaches caller"); }
    catch (InvalidOperationException) { c.That(true, "first failure is preserved"); }
    c.That(await cache.GetAsync("retry") == 23 && await cache.GetAsync("retry") == 23, "later request retries then caches");
    c.That(calls == 2, "failure itself is not cached");
}

static async Task InvalidationGeneration(Checks c)
{
    var oldGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var newGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    var cache = new SingleFlightCache<string, int>((_, _) =>
    {
        var call = Interlocked.Increment(ref calls);
        if (call == 1) { oldStarted.TrySetResult(); return oldGate.Task; }
        return call == 2 ? newGate.Task : Task.FromResult(3);
    }, TimeSpan.FromMinutes(1), 2);
    var oldWaiter = cache.GetAsync("race");
    await oldStarted.Task;
    cache.Invalidate("race");
    var newWaiter = cache.GetAsync("race");
    newGate.SetResult(2);
    c.That(await newWaiter == 2, "new generation completes");
    oldGate.SetResult(1);
    c.That(await oldWaiter == 1, "old waiter may receive its own result");
    c.That(await cache.GetAsync("race") == 2, "old generation cannot overwrite new value");
    cache.Clear();
    c.That(await cache.GetAsync("race") == 3 && calls == 3, "clear removes cached generation");
}

static async Task TtlExpiration(Checks c)
{
    var clock = new ManualClock(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
    var calls = 0;
    var cache = new SingleFlightCache<string, int>((_, _) => Task.FromResult(++calls), TimeSpan.FromSeconds(10), 2, clock);
    c.That(await cache.GetAsync("ttl") == 1, "initial load");
    clock.Advance(TimeSpan.FromSeconds(9));
    c.That(await cache.GetAsync("ttl") == 1 && calls == 1, "value remains live before expiration");
    clock.Advance(TimeSpan.FromSeconds(1));
    c.That(await cache.GetAsync("ttl") == 2 && calls == 2, "first lookup at expiration reloads");
}

static async Task LruCapacity(Checks c)
{
    var calls = new Dictionary<string, int>();
    var cache = new SingleFlightCache<string, int>((key, _) =>
    {
        calls[key] = calls.GetValueOrDefault(key) + 1;
        return Task.FromResult(calls[key]);
    }, TimeSpan.FromHours(1), 2);
    await cache.GetAsync("a"); await cache.GetAsync("b"); await cache.GetAsync("a");
    await cache.GetAsync("c");
    c.That(await cache.GetAsync("a") == 1, "hit refreshes recency");
    c.That(await cache.GetAsync("b") == 2, "least recently used entry is evicted");
    c.That(calls.GetValueOrDefault("a") == 1 && calls.GetValueOrDefault("b") == 2, "capacity remains bounded by LRU policy");
}

static async Task DistinctKeysConcurrent(Checks c)
{
    var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var started = 0;
    var cache = new SingleFlightCache<string, string>((key, _) =>
    {
        if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
        return CompleteAfterGate(key, release.Task);
    }, TimeSpan.FromMinutes(1), 2);
    var a = cache.GetAsync("a"); var b = cache.GetAsync("b");
    var independent = false;
    try { await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); independent = true; }
    catch (TimeoutException) { c.That(false, "distinct key loader start timed out"); }
    c.That(independent && started == 2, "distinct keys enter loaders before either completes");
    release.SetResult();
    c.That(await a == "a" && await b == "b", "distinct loads complete independently");
}

static async Task<string> CompleteAfterGate(string value, Task gate) { await gate; return value; }

sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

sealed class Checks
{
    public int Count { get; private set; }
    public List<string> Failures { get; } = [];
    public void That(bool condition, string message) { Count++; if (!condition) Failures.Add(message); }
    public void Fail(string message) { Count++; Failures.Add(message); }
}
sealed record GroupResult(string Name, bool Passed, int Checks, string[] Failures);
