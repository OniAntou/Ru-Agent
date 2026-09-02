# Shared Workspace Agent Coordination Design

**Date:** 2026-09-02

**Status:** Design approved by user; implementation not started.

## Goal

Allow multiple Ru Agent runs to work concurrently in the same physical
workspace while making file conflicts observable, deterministic, recoverable,
and explicit about changes made by external processes such as Codex or
Opencode.

## Decisions

- Shared mode is opt-in per workspace. Existing workspaces remain exclusive by
  default until the user enables shared mode.
- Concurrent execution is allowed, including `terminal.run` and
  `terminal.shell`, but terminal requests must declare a write-set or be marked
  `read_only`.
- File tools and terminal writes use a base snapshot and a workspace commit
  sequence.
- Text files use three-way merge. When a merge cannot be resolved, the commit
  with the larger coordinator sequence wins.
- Every overwritten version is retained in an encrypted change journal and can
  be restored through an authenticated user action.
- Binary files are never auto-merged. A concurrent binary change fails the
  affected run with `concurrent_binary_conflict`.
- Terminal changes outside the declared write-set are detected after execution.
  Newly discovered text files join the merge transaction; newly discovered
  binary files fail the run.
- Ru identifies another Ru participant by claim/heartbeat. A writer that does
  not participate in the protocol is reported as `unknown_external`; Ru does
  not claim it is Codex or Opencode without an integration signal.
- A filesystem watcher is an early-warning channel only. Hash snapshots and
  the journal are the correctness authority.
- `.git` metadata and coordinator databases are not shared file-tool targets.
  Git mutations are serialized by a repository-level transaction queue.

## Non-goals

- This design is not an OS-enforced sandbox and does not guarantee attribution
  of arbitrary writes to a particular application.
- It does not make binary formats mergeable.
- It does not silently roll back external changes.
- It does not remove the safety boundary for approvals, workspace trust, or
  protected paths.
- It does not make two simultaneous Git index/commit operations safe without
  serialization.

## Current constraint

The current persistence schema has a partial unique index on
`agent_runs(workspace_id)` while `terminal_at_utc IS NULL`. `SqliteHarnessEventStore`
also returns `workspace_busy` before inserting a second active run. Shared mode
must replace this global active-run invariant with a coordinator and resource
claim model; disabling the index alone is not sufficient.

## Architecture

### Workspace coordinator

Each workspace has one logical `WorkspaceCoordinator`. It is not an execution
mutex: model steps and terminal processes from different runs may execute in
parallel. It serializes only claim changes, commit preparation, sequence
allocation, journal writes, and atomic file replacement.

The coordinator exposes four independent operations:

1. `ClaimPaths(runId, paths, baseHashes)` records the paths a run intends to
   write and reports overlapping claims.
2. `ObserveChanges(runId, observation)` records peer/external changes and
   advances the workspace activity stream.
3. `CommitMutation(runId, mutation)` merges and atomically applies a prepared
   mutation under one monotonic `commit_sequence`.
4. `ReleaseClaims(runId)` closes claims on terminal, cancellation, or recovery.

The durable coordinator record contains a workspace version and the next
sequence number. An in-process keyed gate reduces contention, while the SQLite
transaction remains authoritative for multiple host threads.

### Run identity and leases

Every claim contains `workspace_id`, `run_id`, canonical path, access kind,
base hash, lease ID, owner instance nonce, process start time, heartbeat, and
state. The lease ID is never derived from PID alone because Windows can reuse a
PID. Heartbeats are refreshed while a tool or terminal process is active.

An owner that stops heartbeating becomes `orphaned` only after the configured
TTL and process-identity check. Orphan recovery releases its claims and
preserves all already committed journal entries.

## Detection model

### Ru-to-Ru detection

Before a write-capable tool starts, the run submits exact canonical paths and
base hashes. The coordinator returns:

- `claim_granted` when no overlapping write claim exists;
- `workspace_peer_claimed` with the peer run ID and paths when another Ru run
  is already editing the path;
- `workspace_conflict_detected` when a claim overlaps but execution is allowed
  by shared mode.

Claims are advisory for execution and authoritative for commit ordering. The
model-visible event is bounded and contains no private file contents.

While a long-running tool executes, a peer commit emits
`workspace_file_changed_by_peer`. The current run continues unless policy or
the tool contract requires cancellation; its eventual commit must revalidate
all base hashes.

### External detection

For terminal execution, Ru captures the declared write-set before process
start, tracks the complete child process tree, and takes a post-run snapshot.
`FileSystemWatcher`/`ReadDirectoryChangesW` events are debounced and published
as early warnings, but a final hash scan is authoritative because watcher
events can coalesce or be lost.

Any changed path not owned by a live Ru claim is classified as
`unknown_external`. The event includes canonical path, operation category,
before/after hash, and observation sequence, but never invents an application
name. If a future Codex/Opencode adapter registers a claim, the same event is
classified as a known participant instead.

Detection states exposed to Agent and UI are:

- `workspace_peer_claimed`
- `workspace_conflict_detected`
- `workspace_file_changed_by_peer`
- `workspace_external_change_detected`
- `workspace_undeclared_text_discovered`
- `workspace_binary_conflict`
- `workspace_claim_orphaned`

### Read dependencies

The base snapshot records files read as well as files written. If a read
dependency changes before commit, the Agent receives a stale-context event and
the merge engine uses the recorded base. This prevents the model from being
misled into believing it edited the newest content.

## Commit and merge protocol

1. Prepare a mutation with run ID, base snapshot, declared write-set, and
   observed post-run changes.
2. Acquire the coordinator's short commit gate.
3. Re-read current hashes and allocate the next `commit_sequence` in the same
   SQLite transaction as the mutation journal's `prepared` row.
4. For every changed text path, run a three-way merge of base, Agent result,
   and current workspace content.
5. If the merge is clean, atomically replace the file.
6. If the merge has an unresolvable conflict, apply the result of the higher
   commit sequence, save the losing content and base in the encrypted journal,
   and emit `last_writer_wins`.
7. If a binary path has changed since base, abort the affected mutation with
   `concurrent_binary_conflict`; do not replace the binary.
8. For an undeclared changed text path, add it to the same transaction and emit
   `workspace_undeclared_text_discovered`. For an undeclared binary path, fail
   the run and preserve the observed bytes for manual resolution.
9. Mark the journal `committed`, append durable run events, and release claims
   that are no longer needed.

The journal is written before replacement and finalized after replacement. A
startup recovery pass resolves `prepared` entries by checking the expected
post-hash and either finalizing the commit or restoring the recorded preimage.
Recovery never replays an opaque terminal command.

## Persistence model

The implementation adds focused tables rather than growing `agent_runs` with
file coordination details:

- `workspace_coordinators(workspace_id, mode, version, next_commit_sequence,
  heartbeat, updated_at_utc)`
- `workspace_run_members(workspace_id, run_id, owner_instance_nonce, state,
  heartbeat, created_at_utc, released_at_utc)`
- `workspace_path_claims(claim_id, workspace_id, run_id, canonical_path,
  access_kind, base_hash, lease_id, heartbeat, state)`
- `workspace_file_snapshots(snapshot_id, run_id, canonical_path, content_hash,
  content_payload_id, observed_at_utc)`
- `workspace_change_journal(change_id, workspace_id, run_id,
  commit_sequence, canonical_path, before_hash, after_hash, base_hash,
  before_payload_id, after_payload_id, state, created_at_utc)`
- `workspace_mutations(mutation_id, workspace_id, run_id, state,
  expected_paths, commit_sequence, created_at_utc, committed_at_utc)`

Canonical paths, hashes, payload size limits, encryption, and redaction reuse
existing Harness persistence rules. Journal payloads are authenticated and
never exposed through model-visible events.

## API and protocol changes

Existing `POST /agent/v1/runs` remains source-compatible. In exclusive mode it
keeps returning `workspace_busy`. In shared mode it returns coordinator mode
and version in the run response.

New authenticated user-control endpoints:

- `GET /agent/v1/workspaces/{workspaceId}/activity`
- `GET /agent/v1/workspaces/{workspaceId}/changes`
- `POST /agent/v1/workspaces/{workspaceId}/changes/{changeId}/restore`
- `PUT /agent/v1/workspaces/{workspaceId}/concurrency`

The concurrency endpoint accepts only `exclusive` or `shared`, requires the
workspace's current version, and records an audit event. Enabling shared mode
does not terminate existing exclusive runs; it takes effect for the next run
after the coordinator state is initialized.

Tool contract changes:

- File tools derive exact write-sets and base hashes internally.
- `terminal.run` and `terminal.shell` accept either `read_only: true` or a
  bounded `write_set` of canonicalizable paths.
- Missing both fields returns `terminal_write_set_required`.
- `.git`, database files, and protected paths are rejected from a write-set.
- Events contain IDs, paths, hashes, and bounded codes only; no raw terminal
  output or private file content is sent to the model as coordination data.

## Migration and rollout

1. Add schema versioning, coordinator tables, journal encryption, and recovery
   code without changing the current unique workspace index.
2. Add observation-only claims and activity events while all workspaces remain
   exclusive.
3. Add the per-workspace setting with default `exclusive` and migrate existing
   workspace rows deterministically.
4. Enable shared mode for a test workspace and run fault-injection, watcher-loss,
   stale-lease, text-conflict, binary-conflict, and crash-recovery tests.
5. Enable production opt-in. Keep a guarded migration path that can return a
   workspace to `exclusive` after all active shared runs become terminal.
6. Drop the partial unique active-workspace index only after the shared-mode
   migration gate passes and the coordinator transaction path is authoritative.

## Testing strategy

### Unit and persistence tests

- Atomic sequence allocation under concurrent commits.
- Exact-path claim overlap and release semantics.
- Lease heartbeat, PID reuse protection, TTL expiry, and orphan recovery.
- Three-way clean merge and deterministic last-writer-wins conflict handling.
- Encrypted journal backup/restore and prepared-transaction recovery.
- Text encoding/newline preservation and bounded payload limits.
- Binary conflict rejection and protected path rejection.

### Integration tests

- Two Ru runs edit different text files concurrently and both complete.
- Two Ru runs edit the same text file and the larger commit sequence wins with
  a restorable journal entry.
- A terminal writes an undeclared text file and it is merged and audited.
- A terminal writes an undeclared binary file and the run fails without binary
  replacement.
- A watcher event is dropped; the final hash scan still detects the change.
- An external writer changes a file and Ru reports `unknown_external` without
  naming Codex/Opencode.
- A process dies before, during, and after commit; recovery leaves no half-file.
- Exclusive mode retains the current `workspace_busy` behavior.
- Switching a workspace back to exclusive blocks new shared runs only after
  the existing shared runs drain.

### Manual acceptance

- Run two Ru Agents in one opt-in workspace and observe peer claims/events.
- Edit the same text file from Ru and an unrelated editor; verify external
  detection, last-writer-wins, and journal restore.
- Run Codex or Opencode against the same directory; verify Ru reports an
  unknown external writer rather than asserting the wrong identity.
- Confirm model context, UI activity, audit logs, and restore controls contain
  no secret payloads or raw terminal output.

## Risks and mitigations

- Last-write-wins can hide a logical code conflict. The journal, visible event,
  and restore action are mandatory mitigations.
- Watchers can miss events. Final hash scans and commit-time revalidation are
  authoritative.
- Arbitrary terminal commands can mutate outside the declared set. The design
  detects additional text changes and fails on binary changes, but it is not an
  OS sandbox.
- A large workspace can make full scans expensive. The implementation must
  bound snapshots to tracked/declared roots and use incremental watcher state;
  it must not silently omit a path from a claimed transaction.
- Git metadata can be corrupted by concurrent commands. Repository-level Git
  transactions remain serialized even in shared mode.

## Acceptance criteria

Shared mode is ready for implementation completion only when all of the
following are true:

1. Two Agents can execute concurrently in one opted-in physical workspace.
2. Every overlapping Ru claim is visible to the other Agent before commit.
3. Unknown external writes are detected without false application attribution.
4. Text conflicts resolve deterministically by coordinator sequence and retain
   a restorable losing version.
5. Binary conflicts never silently overwrite one another.
6. Terminal write-set drift is detected and audited.
7. Crash recovery leaves the workspace and journal in a consistent state.
8. Exclusive workspaces preserve the existing safety behavior.
9. All API, UI, audit, and model-visible outputs remain bounded and redacted.
