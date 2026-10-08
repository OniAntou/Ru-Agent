# Lease renewal can extend stale ownership and stale releases can revoke a renewed lease

The coordination service uses a versioned lease to ensure only the current owner can renew or release a resource. The current implementation has several related race defects: acquisition replaces a live owner, renewals do not consistently enforce expiry and version, and a release based on an old observation can remove a newer lease.

## Reproduction

Use the public `LeaseRegistry` methods in `src/`. A caller acquires `build/42` as `worker-a`, renews it, and later submits the release request it prepared before the renewal. The stale request currently removes the live renewed lease. Separately, a second owner can acquire the same resource before the original expiry, and an owner can renew after its lease has expired.

## Expected behavior

- A live lease remains owned by its current owner until its expiry. A competing acquisition reports the existing owner and does not change the lease.
- Expiry is evaluated against the injected clock. At the expiry instant the lease is expired. An expired lease cannot be renewed or released; a new owner may acquire it.
- Renew and release require the current owner and exact version. A successful renewal increments the version exactly once and sets expiry to `now + duration`. A stale release cannot remove that renewed lease.
- Every mutation is atomic under concurrent calls. With the same expected version, competing renew/release operations cannot both succeed.
- Empty identifiers and non-positive durations are rejected without changing state.

Keep the public types and method signatures in the repository. Do not add timers, sleeps, wall-clock reads in tests, or external packages. Use the injected `TimeProvider` so boundary behavior is deterministic.
