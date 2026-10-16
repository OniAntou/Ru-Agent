---
status: accepted
date: 2026-10-16
---

# Always allow parallel sessions in one workspace

Different conversations are independent units of work, even when their working
directory is identical. Requiring users to find and enable shared coordination
made switching between running sessions appear unsupported.

Workspace coordination is always shared. Admission remains atomic per session:
idempotency replay precedes the active-session check, and a second active run
in the same session is still rejected. Different sessions no longer contend
for an exclusive workspace admission lock, including compatibility hosts that
do not compose the coordinator store.

The existing membership scheduler bounds execution, with 32 workspace slots by
default and the unchanged global limit of four drive workers. The concurrency
API retains its route and fields but accepts only shared mode and capacities
from 2 to 32. Requests for exclusive mode or one slot fail with the existing
named policy-validation error. Schema v19 upgrades legacy exclusive/single-slot
coordinators; existing shared capacity choices above one remain intact.

Workspace trust, permission presets, per-session grants, approvals, provider
pins and tool execution authority retain their existing owners. A busy or parked
session does not block an unrelated session in the same directory. Cancellation
and client navigation continue to target a run/session, not the workspace.

All sessions see the same real filesystem. Existing hash guards and external
change detection remain in force; this decision does not introduce worktrees,
per-path locks, automatic merge or OS confinement. Those need separate designs.

An opt-in GUI toggle was rejected: parallel sessions are normal product behavior.
Unlimited execution was also rejected because the scheduler must retain bounded
provider, process and memory usage.
