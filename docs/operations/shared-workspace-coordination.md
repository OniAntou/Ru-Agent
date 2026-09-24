# Shared workspace coordination

Ru keeps `exclusive` as the default workspace mode. `shared` is an explicit
workspace policy selected through `PUT /agent/v1/workspaces/{workspaceId}/concurrency`.
The coordinator persists a monotonic commit sequence in SQLite and uses a
renewable membership lease for each active Ru run.

## Trust and preset boundary

Workspace trust is independent of workspace coordination and autonomy. The
canonical root must receive one explicit trust decision for its
`WorkspaceIdentity`; the GUI confirms it and CLI `run`/`chat` use
`--trust-workspace`. A moved or recreated root is a new identity and must be
trusted again. Session-scoped folder grants and preset selections are not shared
with another/forked session, while the explicit trust decision follows the
canonical identity. Full Access additionally requires the GUI gate or CLI
`--confirm-full-access`.

The canonical profiles are `plan` (workspace read/Web auto; terminal and
mutation deny), `standard` (bounded-local in-workspace mutation auto;
external/escape ask), and `full_access` (external structured file and arbitrary
terminal auto; structured protected paths deny). Full Access terminal runs as
the current Windows user, can affect data outside the workspace, and is not a
network/path sandbox. Hard timeout, output cap, cancellation, and process-tree
cleanup remain enforced. Shared coordination still serializes known claims and
journals authoritative external changes, but it cannot infer every side effect
of an arbitrary command.

External tools such as Codex, Opencode, editors, and build processes are not
identified by process name. Ru detects their file changes by comparing an
authoritative SHA-256 hash with the expected workspace hash and records the
event as `workspace_external_change_detected` with `writer_kind=unknown_external`.
The encrypted journal is visible through:

- `GET /agent/v1/workspaces/{workspaceId}/activity`
- `GET /agent/v1/workspaces/{workspaceId}/changes`

During the next Agent model step, the host also adds a bounded, explicitly
untrusted summary of recent external journal entries to the system-prompt
runtime references. Ru therefore knows that a file changed, its sequence, and
the before/observed hashes without being told an unverified process identity.

Use `after_sequence` and `limit` for bounded polling. A `run_id` is present for
Ru-owned changes; an absent `run_id` identifies an external writer. Binary
overlap is fail-closed. The bounded text merge engine uses the coordinator
sequence for overlapping hunks, and restore verifies the current hash before
replacing bytes, writes an authenticated restore journal entry, and is safe to
retry.

Terminal requests may carry additive `read_only` and `write_set` metadata.
Malformed paths and a non-empty write set on a read-only request are rejected.
The runtime acquires/releases the per-run lease and releases any path claims
transactionally on shutdown. A terminal mutation with `write_set` claims its
normalized paths before process start and releases them after completion;
overlapping binary claims fail closed. Legacy calls continue through the
conservative compatibility path.

For troubleshooting, check the activity sequence first, then compare the
before/after hashes. If the sequence is present but the UI is stale, refresh the
bounded activity feed; the Chat surface shows mode, active claim count,
peer/external entries, and sequence numbers without inferring external process
identity.

## Acceptance checklist

- Ru + editor/Codex/Opencode: mutate a file outside the Ru process; expect
  `workspace_external_change_detected`, `unknown_external`, and a changed hash.
- Two Ru peers with disjoint claims: both memberships remain active and their
  sequences are monotonic.
- Overlapping text: merge is deterministic by coordinator sequence.
- Overlapping binary: `concurrent_binary_conflict`; working bytes are unchanged.
- Crash/restart: startup reconciliation expires leases and rejects abandoned
  prepared journal rows without duplicating a sequence.
- Restore: supply the currently observed SHA-256; stale hashes receive
  `workspace_restore_stale`, while a matching hash produces a journaled restore.
