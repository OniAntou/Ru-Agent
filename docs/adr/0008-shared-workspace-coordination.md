---
status: accepted
date: 2026-09-02
amended_by: ADR-0020 always allow parallel sessions in one workspace
  (`0020-always-parallel-workspace-sessions.md`, accepted 2026-10-16), for the
  exclusive default and single-slot capacities; shared coordination is now
  always on and the coordinator design below remains in force.
---

# Shared workspace coordination

> Amended by [ADR 0020](0020-always-parallel-workspace-sessions.md) on
> 2026-10-16: workspace coordination is always shared, so the "exclusive mode
> remains the default / shared mode is opt-in" wording in the Decision section
> below is historical. Policy validation accepts only shared mode with a
> capacity from 2 to 32, and schema v19 rewrites legacy exclusive/single-slot
> rows at startup. The durable coordinator, membership leases, snapshots and
> change journal described here remain in force.

## Decision

Use one durable coordinator per `workspace_id`. Exclusive mode remains the
backward-compatible default; shared mode is opt-in. Membership leases, path
claims, snapshots, and change journal rows live in the existing SQLite harness
database. Changes receive a monotonic coordinator sequence instead of relying
on wall-clock order.

External writers are intentionally reported as `unknown_external`. File-system
signals cannot reliably prove whether a change came from Codex, Opencode, an
editor, or a build process, so the authoritative signal is the observed hash.

## Consequences

- Detection is durable and workspace-scoped, and activity can converge after a
  client reconnects.
- Text merge is bounded and deterministic; binary conflicts fail closed.
  Restore is hash-guarded, authenticated, journaled, and retry-safe. Startup
  reconciliation expires leases, releases stale claims, and rejects abandoned
  prepared journal rows.
- OS-level filesystem/network sandboxing is out of scope for this feature. The
  terminal still runs under the current Windows user; a future sandbox must use
  an OS-enforced process-tree, filesystem, and network boundary.
