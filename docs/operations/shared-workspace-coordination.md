# Shared workspace coordination

Ru always uses `shared` workspace coordination. Different sessions can run in
one workspace without a toggle or an extra approval; each session still owns
at most one non-terminal run. `PUT /agent/v1/workspaces/{workspaceId}/concurrency`
accepts only shared mode, with a membership capacity from 2 to 32 (default 32).
The Host's global worker limit remains four concurrent drive passes; additional
work waits for worker/membership capacity without cancelling other sessions.
The consolidated v18-to-v29 upgrade converts existing exclusive or single-slot coordinators in place,
preserving runs, trust, grants, lease settings and shared capacities above one.
The coordinator persists a monotonic commit sequence in SQLite and uses a
renewable membership lease for each active Ru run.

## Trust and preset boundary

Workspace trust is independent of workspace coordination and autonomy. The
canonical root must receive one explicit trust decision for its
`WorkspaceIdentity`; the GUI confirms it and CLI `run`/`chat` use
`--trust-workspace`. A moved or recreated root is a new identity and must be
trusted again; the GUI offers a one-click re-trust prompt that names the
identity change, and `--trust-workspace` re-approves it for automation.
Session-scoped folder grants and preset selections are not shared
with another session, while the explicit trust decision follows the
canonical identity. Full Access additionally requires the GUI gate or CLI
`--confirm-full-access`.

The canonical profiles are `plan` (workspace read/Web auto; terminal and
mutation deny outright, read-only; external structured access asks), `standard`
(bounded-local in-workspace mutation auto; external/escape ask; terminal uses
the existing classifier and approval rules), and
`full_access` (external structured files, arbitrary terminal, and structured
protected paths — harness key/database files and runtime descriptors — are
automatic). Full Access terminal runs as
the current Windows user and may access paths and network resources available
to that user; there is no OS sandbox. Hard timeout, output cap, cancellation,
and process-tree cleanup remain enforced. Shared coordination bounds
active runs through membership leases and journals authoritative external
changes, but it cannot infer every side effect of an arbitrary command.

External tools such as Codex, Opencode, editors, and build processes are not
identified by process name. Ru detects their file changes by comparing an
authoritative SHA-256 hash with the expected workspace hash and records the
event as `workspace_external_change_detected` with `writer_kind=unknown_external`.

Driving runs in the same workspace share one captured baseline and watcher.
Joining a drive does not recapture over a peer's baseline; parking, completion
or setup failure only releases that drive's lease. The final release stops the
watcher and clears its baseline, so a later drive captures fresh content.
An already-running journal callback from a retired watcher cannot acknowledge
its hash into a replacement baseline. This lifetime does not grant file access
or provide filesystem isolation.

The encrypted journal is visible through:

- `GET /agent/v1/workspaces/{workspaceId}/activity`
- `GET /agent/v1/workspaces/{workspaceId}/changes`

During the next Agent model step, the host also adds a bounded, explicitly
untrusted summary of recent external journal entries to the system-prompt
runtime references. Ru therefore knows that a file changed, its sequence, and
the before/observed hashes without being told an unverified process identity.

Use `after_sequence` and `limit` for bounded polling. A `run_id` is present for
Ru-owned changes; an absent `run_id` identifies an external writer. Restore
verifies the current hash before replacing bytes, writes an authenticated
restore journal entry, and is safe to retry.

Terminal requests may carry additive `read_only` and `write_set` metadata.
Malformed paths and a non-empty write set on a read-only request are rejected.
The runtime acquires the per-run membership lease before the pass, renews it by
heartbeat, and releases it when the pass stops. `write_set` paths are validated
but not claimed: membership leases and `max_concurrent_runs` bound simultaneous
execution rather than acquiring per-path claims. Sessions share the same real
files. Hash guards and external-change notices do not provide worktree isolation
or automatic merging of competing terminal writes.

What the journal keeps is bounded. The baseline taken when a run starts skips files that churn
on their own (database files and their `-wal`/`-shm`/`-journal` companions, `.log`, `.tmp` and `.lock`
files, and `.git`, `node_modules`, `__pycache__` and `.vs` folders, plus the Ru profile when the
workspace is a home folder), so those never enter the journal. A baseline keeps a file's previous
content only up to 1 MiB; a larger file keeps its hash, and a restore of it reports that the content
is not retained. A retention pass runs shortly after the Host starts and every six hours: it removes
settled entries (`committed`, `restored`, `rejected`) older than 14 days, then trims each workspace to
64 MiB of retained content, oldest first. A `prepared` entry is in flight and is never pruned. The
pass frees space inside the database file for reuse; it does not shrink the file itself.

For troubleshooting, check the activity sequence first, then compare the
before/after hashes. No GUI or CLI surface renders the coordination state:
poll the bounded activity feed directly to see peer/external entries and
sequence numbers without inferring external process identity.

## Acceptance checklist

- Ru + editor/Codex/Opencode: mutate a file outside the Ru process; expect
  `workspace_external_change_detected`, `unknown_external`, and a changed hash.
- Two Ru sessions in the same workspace with no configuration: both
  memberships remain active and their sequences are monotonic.
- Crash/restart: startup reconciliation expires leases and rejects abandoned
  prepared journal rows without duplicating a sequence.
- Restore: supply the currently observed SHA-256; stale hashes receive
  `workspace_restore_stale`, while a matching hash produces a journaled restore.
