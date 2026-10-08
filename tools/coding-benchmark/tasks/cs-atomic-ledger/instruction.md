# Atomic ordered ledger batches

Implement the C# API in `src/` for an in-memory ledger. Keep the public types
and method signatures in the starter files. You may add focused source files.
The workspace is offline; do not add package dependencies.

`AtomicLedger` is constructed from account names and non-negative opening
balances. `Apply(requestId, expectedVersion, transfers)` processes one ordered
batch. Every transfer must have distinct existing accounts and a positive
amount. Transfers observe earlier transfers in the same batch. The entire
batch either commits once or has no effect. A successful non-empty batch
increments the ledger version exactly once; an empty batch is invalid.

The ledger begins at version zero. A new request ID must match the current
version or return `version_conflict`. A successful request is idempotent:
repeating the same request ID and structurally identical ordered transfer body
returns the original result, even if the supplied expected version is now
stale. Reusing an ID with any different body returns `idempotency_conflict`.
Rejected requests do not reserve their IDs. Validation, insufficient funds,
and checked 64-bit overflow reject the whole batch without changing state.
Balances may never be negative. For a transfer that lacks debit funds and
would also overflow the destination, check funds first and return
`insufficient_funds`. Reject self-transfers and unknown accounts as `invalid`.

Every result contains a point-in-time immutable snapshot: later ledger writes
must not change either its version or balances, and callers must not be able to
mutate the snapshot through a cast to a mutable dictionary. The ledger must be
linearizable under concurrent calls: two distinct successful updates based on
the same version cannot both commit.

Return codes are exactly `ok`, `invalid`, `version_conflict`,
`idempotency_conflict`, `insufficient_funds`, or `overflow`. Do not throw for
request-level failures. Constructor input with a negative balance or duplicate
account is invalid and may throw `ArgumentException`.

Run the visible smoke test with `dotnet run --project smoke/Smoke.csproj`.
The external grader checks deterministic batches, replay identity, rejection
atomicity, overflow boundaries, snapshot immutability, and barrier-synchronized
concurrency. It reports seven independent groups.
