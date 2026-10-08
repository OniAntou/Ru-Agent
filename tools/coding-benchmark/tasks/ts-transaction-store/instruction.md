# Implement an optimistic transactional store

Implement `TransactionStore` in `src/store.ts` with no package dependencies.
The store holds string keys and string values. A mutation is `{ op: "set",
key, value }` or `{ op: "delete", key }`; batches are ordered and non-empty.

`transact(requestId, expectedVersion, mutations)` validates the full request,
then applies all mutations to a private copy. Keys and request IDs must be
non-empty strings; set values must be strings; unknown operations and malformed
records return `invalid`. Deletes of absent keys are valid. A valid new request
must match the current version or returns `version_conflict`. A successful
batch increments the version once. No rejected batch may partially change
state or reserve its request ID.

Successful request IDs are idempotent: the same ordered mutation sequence
returns the original result even with a stale expected version; reusing the ID
with a different sequence returns `idempotency_conflict`. Results and
`snapshot()` contain an immutable point-in-time values record and its version.
Concurrent calls are linearizable: at most one distinct batch based on a given
version can commit. Return codes are exactly `ok`, `invalid`,
`version_conflict`, and `idempotency_conflict`.

Run `node smoke.ts` for the visible example. The external grader checks ordered
updates, atomic rejection, replay binding, stale versions, generated batches,
snapshot isolation, and concurrent same-version writers. Edit only
`src/**/*.ts`.
