---
status: accepted
date: 2026-11-13
amended: 2026-11-28
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

### Opt-in

1. A root may spawn a **detached** child only when the user asked for background work
   in the root prompt. `agent_spawn` takes `detached=true` together with
   `detached_request`, the user's own words (at most 256 characters). The Host refuses
   the spawn with `subagent_detached_user_request_required` unless those words occur, as
   whole words in order, in the authenticated root prompt (case, whitespace and
   punctuation ignored; at least two words and six letters or digits). Like a model override, this proves provenance, not intent: the
   parent still judges whether the user actually asked. Workspace instructions, tool
   results, child briefs and assistant history cannot supply the words. There is no
   session setting, slash command or GUI toggle (amended 2026-11-27: the proposed
   per-session switch was replaced by the prompt request alone).

### Lifecycle

2. When the creating root ends normally (completed or failed), its attached children
   are canceled as before and its detached children keep running under the Host
   scheduler. Schema v43 marks them in `agent_subagent_detached`. An explicit cancel of
   the root (also after it ended, and any root that ends `cancelled`), `agent_cancel` from
   any root of the session, session deletion and Host shutdown still stop them.
3. Detached children do not survive a Host exit. On the next start, recovery ends an
   interrupted detached child whose root already ended with
   `subagent_detached_interrupted`; its earlier results stay readable.
4. The child keeps the workspace, policy snapshot, model pin and tool projection it was
   admitted with, and keeps recording into the family's last budget window. Detaching
   adds no tool, scope, approval, path or network permission. Approval-requiring work
   fails closed with `subagent_parent_action_required` and is reported in the child's
   result instead of waiting forever.

### Broker authority

5. While the spawning parent is live, the Host asks the Broker to retain that parent's
   lease snapshot (`RetainDetachedParentAsync`). A detached binding is evaluated against
   the live parent while that lease is open, so a parent policy change still revokes
   it. After a normal close of the parent's lease, the Broker evaluates the detached
   child against the retained snapshot intersected with the ceiling captured at spawn.
   Only a ceiling-bound detached bind may use a retained snapshot.
6. Revocation stays explicit: canceling the child's own run, closing the parent's
   session, or disposing the runtime revokes a detached child. Retained snapshots are
   bounded at 1,024; at capacity only a snapshot no live detached child still uses is
   dropped, otherwise the spawn is refused with `subagent_delegation_capacity`. A
   retained snapshot grants nothing by itself and is never widened.

### Later roots of the same session

7. Detached children are owned by the session. A later root of the same session sees
   them in its `run_state` reference and may `agent_wait` for them, `agent_cancel` them,
   or `agent_apply` a coding result on the user's request. The result and apply journal
   stay keyed by the creating root; the applying root's own policy and approval govern
   the write. The Broker accepts a foreign parent run only when it still holds that
   parent's retained snapshot in the applying root's session and workspace; after a Host
   restart or eviction the apply is denied (`subagent_apply_denied`).
8. A later root of the same session may `agent_send` a follow-up to a detached child. The
   send is admitted under the later root's live lease, but the continuation runs under the
   creating root's family, pinned policy and internal session; the later root cannot widen
   the child. The message waits until the current attempt is terminal, then becomes a new
   attempt. A cancelled family refuses with `subagent_canceled`; a root in another session
   cannot reach the child (`subagent_not_found`); an attached child of a finished root still
   refuses (`subagent_root_inactive`).
9. The HTTP subagent summary and the SDK type report `detached` on each child; the GUI
   and CLI do not label it yet. The GUI stays an observer; there are no user
   Send/Stop/Apply controls. The user asks the main agent.

### Manual PR integration

After a result is applied, Ru may prepare a **manual** integration artifact: a
bounded unified patch and a branch/commit suggestion. Ru does not create a GitHub
pull request, push a branch, or write remote state. The user applies or exports the
patch and creates the PR in their own Git hosting flow. This keeps credentials,
network authority and repository history outside the model path.

## Invariants

- Exactly one execution authority remains: Broker execution through the Host's
  gateway composition.
- Detached children do not survive a Host process exit.
- Existing per-family limits (any configured concurrency cap, attempts, workspace and
  snapshot bounds) apply to detached families too; detaching only removes the
  normal-root-end cancellation.
- A detached child cannot escalate to `full_access`, choose another provider/model,
  or apply its own changes.
- The change review is read-only and bounded; exporting a patch grants no execution
  authority.

## Alternatives

- **Keep every child under its root and offer no background work:** safe but cannot
  cover long-running work.
- **A per-session toggle in addition to the prompt request:** more UI and persisted
  state for the same evidence; the prompt request already requires the user's own
  words.
- **Session-scoped Broker anchor lease:** a long-lived lease per session would keep
  live authority around after every root ends; the retained snapshot keeps authority
  frozen at what the parent had.
- **Vendor/cloud background agents:** would add a second execution authority,
  remote credentials and storage, contrary to the local-first boundary.
- **Automatic commits/PRs:** rejected; they would need explicit GitHub credentials,
  remote network authority and a separate audit path.

## Consequences

Schema v43 adds `agent_subagent_detached`. Child visibility queries accept a detached
child of an earlier root in the same session for wait, cancel, send and apply. A later root
can continue another root's detached child under that child's creating-family policy, but
cannot change its pin or family. A detached result cannot be applied after a Host restart;
both refusal paths fail by name rather than falling back.
