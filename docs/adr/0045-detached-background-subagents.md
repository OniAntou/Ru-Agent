---
status: proposed
date: 2026-11-13
---

# Detached background subagents and manual PR integration

## Context

[ADR 0022](0022-delegated-subagent-runs.md) binds every child to one root run. The
family is canceled when the root becomes terminal, and `agent_apply` requires the
owning root's lease. That is the right default for a delegated task the parent is
waiting on, but it cannot model work that should continue after the root answers:
long builds, repository-wide reviews, or a coding attempt the user wants to inspect
later.

Cloud coding agents offer this as a separate background-agent product, usually with
vendor-side execution, storage and pull-request creation. Ru is local-first: the Host,
Broker and workspace are on the user's machine, and this repository's authority model
does not permit a second execution path. This ADR records the bounded local version
and explicitly leaves automatic PR creation out.

## Decision

### Detached child lifecycle

1. A root may request a **detached** child only when the user has explicitly enabled
   background subagents for the session and asked for one in the root prompt. The
   session setting defaults to off; a model cannot enable it, and a workspace
   instruction, tool result or child brief cannot supply the request.
2. A detached child is owned by the **session**, not only by the creating root. When
   the creating root becomes terminal, its detached children continue under the Host
   scheduler. An explicit root/session cancel still cancels them; a Host shutdown
   stops them with the rest of the process.
3. The child keeps the workspace, policy snapshot, model pin and tool projection it
   was admitted with. Detaching adds no tool, scope, approval, path or network
   permission. Approval-requiring work in a detached child fails closed when no live
   root can answer it; the child reports the blocked receipt instead of waiting
   forever.
4. Results, usage and change sets are stored as they are today, but indexed by
   session as well as by the creating root. A later root run in the same session may
   list a detached result and, only on the user's explicit instruction, apply it
   through the existing `agent_apply` path. The applying root's policy still governs
   the write; nothing is applied automatically.
5. The GUI is an observer: it may show detached tasks, progress and the read-only
   change review, and may export the review. Direct Send/Stop/Apply controls remain
   unavailable; the user asks the main agent, which is the only control surface.

### Manual PR integration

After a result is applied, Ru may prepare a **manual** integration artifact: a
bounded unified patch and a branch/commit suggestion. Ru does not create a GitHub
pull request, push a branch, or write remote state. The user applies or exports the
patch and creates the PR in their own Git hosting flow. This keeps credentials,
network authority and repository history outside the model path.

## Invariants

- Exactly one execution authority remains: Broker execution through the Host's
  gateway composition.
- Detached children do not survive a Host process exit; durable recovery extends to
  the next Host start, not to cloud execution.
- Existing per-family limits (concurrency, identities, attempts, workspace/snapshot
  bounds) apply to detached families too; the session setting only removes the
  parent-terminal cancellation rule.
- A detached child cannot escalate to `full_access`, choose another provider/model,
  or apply its own changes.
- The change review is read-only and bounded; exporting a patch grants no execution
  authority.

## Alternatives

- **Keep every child under its root and offer no background work:** safe but cannot
  cover long-running work; this ADR would not be needed.
- **Vendor/cloud background agents:** would add a second execution authority,
  remote credentials and storage, contrary to the local-first boundary.
- **Automatic commits/PRs:** rejected for the first implementation; they would need
  explicit GitHub credentials, remote network authority and a separate audit path.
  Manual patch export is the smallest useful step.

## Consequences

Detached children require new session-level ownership fields, a session-scoped
view/apply path, scheduler changes for parent-terminal families, and a guard that
stops approval-dependent work in a detached child. Those changes need schema
migration, public-contract updates for the observation/apply routes, recovery and
cleanup tests, and updates to `docs/operations/subagents.md`. Until then, the current
root-bound behavior remains unchanged and the manual patch export is available from
the signed change review.
