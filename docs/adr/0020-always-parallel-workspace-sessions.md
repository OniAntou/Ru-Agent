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
default and a global limit of drive workers (four when accepted; sixteen since
2026-10-22, because a drive worker is held through a whole terminal command and
four builds stalled every other session and subagent). Model calls keep their
separate provider-step bound. The concurrency
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

## Amendment (2026-11-16): no execution ceiling

At the owner's request the "unlimited execution was rejected" decision above is
reversed. The runtime loop no longer has a drive-worker limit (formerly sixteen), the
provider-step bound has no default (formerly four; `RU_MAX_CONCURRENT_MODEL_STEPS` remains
as an opt-in cap, for example 1 for a single-slot local runtime), and a workspace has no
membership cap by default (formerly 32). The concurrency API accepts shared mode with
`max_concurrent_runs` of 0 (no cap, the default) or any cap of two or more; schema v37
rebuilds `workspace_coordinators` for that and moves rows at the old 32 ceiling to no cap
while keeping smaller user caps. Exclusive mode and single-slot caps stay rejected.

The trade-off the original text named is accepted: provider, process and memory use are
no longer bounded by the scheduler. Provider rate limits, the machine's resources, the
per-run step and terminal bounds, and an optional cap are what remain. Session admission,
trust, permissions and execution authority are unchanged.
