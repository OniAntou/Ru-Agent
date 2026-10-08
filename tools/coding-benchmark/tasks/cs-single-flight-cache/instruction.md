# Concurrent single-flight cache

Implement the generic API in `src/` without packages or network access. Keep
the starter constructor and method signatures. `GetAsync` must coalesce
simultaneous loads for the same key while allowing distinct keys to load
concurrently. Successful values are cached for the configured positive TTL;
the first lookup at or after expiration starts a new load. Cache capacity is a
positive maximum and eviction uses least-recently-used order, with hits
refreshing recency.

Each caller's cancellation token cancels only that caller's wait. It must
never cancel the shared loader or other waiters. Loader failures are delivered
to the callers in that flight, are not cached, and allow a later request to
retry. Loader delegates receive `CancellationToken.None` because this cache
has no shared lifetime token.

`Invalidate(key)` immediately removes the cached value and current flight from
the lookup path. `Clear()` does so for all keys. Work already awaiting an old
flight may receive its result, but a load that began before invalidation or
clear must never publish into the cache or displace a newer flight's value.
Constructor arguments require a non-null loader, positive TTL, and positive
capacity; invalid arguments throw `ArgumentException` or
`ArgumentNullException`.

Use the optional `TimeProvider` for all expiration decisions. No wall-clock
sleep or timing guess is needed. The visible smoke check is
`dotnet run --project smoke/Smoke.csproj`; the external grader uses barriers,
task completion sources, and a manual clock. Its eight groups independently
check constructor validation, flight sharing, cancellation, retry, invalidation
generations, TTL, LRU, and concurrency across keys.
