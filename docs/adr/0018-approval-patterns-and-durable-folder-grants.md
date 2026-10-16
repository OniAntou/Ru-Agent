---
title: Remembered approval patterns and durable folder grants
status: accepted
date: 2026-10-13
amended_by: ADR-0021 full access is literally full
  (`0021-full-access-is-literally-full.md`, accepted 2026-10-17), for the
  structured protected-path statements only.
---

# ADR 0018: Approval patterns and durable folder grants

> Amended by [ADR 0021](0021-full-access-is-literally-full.md) on 2026-10-17:
> `full_access` is literally full, so the statements below that the structured
> protected path "still deny at every preset" or that a remembered pattern
> remains subject to "protected paths" apply to `plan` and `standard` only.

## Context

Approval memory was exact-intent. `allow_always` persisted the full reviewed
intent (tool id, exact path, or the complete argv with its cwd) as a
project-scope allow rule, so any argument change asked again, and an
external-directory ask could only open a session-scoped Broker folder grant
that died with the process and was never shared with later sessions. The user
reviewed the alternatives and asked for two bounded widenings at
Claude-Code-like friction: remember a command pattern instead of one exact
argv, and let an approved external folder stay approved for the workspace.
Approval lifetime and grant durability are lasting permission decisions, so
they are recorded here.

## Decision

`allow_always` now remembers a bounded pattern derived from the reviewed call,
never wider than the reviewed shape:

- A terminal `mode=run` call stores the resolved executable plus at most two
  leading non-flag argv tokens (`git status`, `npm run build`) with the exact
  canonical launch directory. Flag-first invocations (`node -e ...`) and
  `mode=shell` scripts stay exact: a prefix of arbitrary shell text is not a
  safe pattern, and the runtime keeps the same fail-closed habit that made
  Claude Code repair its compound-command handling instead of copying it.
- An external-directory ask stores the nearest existing ancestor folder of the
  approved target (a file grants its parent), canonicalized through the final
  path and represented as the path pattern `<folder>\**`. The pattern covers
  the folder's subtree for every structured tool, matching what the
  session-scoped Broker grant already covers. The Broker session grant is
  still issued for the current session; the durable project rule covers later
  runs, later sessions of the same workspace, and host restarts.
- `PolicySnapshotEvaluator` honors a matched allow rule for external
  structured targets (`external_directory_rule_allow`). Previously only deny
  rules were taken from the ordered rules outside the workspace. Precedence is
  unchanged: deny before ask before allow, the plan read-only gate applies
  before the rule verdict, and structured protected paths still deny in `plan`
  and `standard` (ADR-0021).
- `rule_asks` and `terminal_protected_path_requires_approval` never become a
  durable rule. The Host refuses the save (degrading to `allow_once` with
  `rule_saved: false`) instead of relying on clients to hide the choice. The
  in-process Session Approval Memory keeps the exact intent for plain allows
  and stores the bounded pattern only when `allow_always` actually saved a
  durable rule; external-directory approvals keep the Broker folder grant and
  write no ledger entry.

The interactive surfaces offer both folder decisions for an external ask
(session grant and project remember); the CLI additionally stops hiding the
folder choice it already documented.

## Boundary and consequences

A remembered pattern is a permission the user explicitly chose in one review.
It remains subject to deny rules, ask rules, the plan gate, protected paths
(in `plan` and `standard`), hard-invalid requests, and every execution-time
invariant; the in-flight call
is still authorized by the Broker's single-use token, not by the saved rule.
Permission lifetime changes: an external folder approval can now outlive the
session and the host process for the same canonical workspace identity, and a
terminal pattern can authorize later matching runs until the project rule is
deleted. Deleting the rule (or the workspace identity changing) removes the
grant. `allow_once` and `allow_session_directory` keep their existing scopes,
single-use authorization and snapshot binding are unchanged, and no route,
tool, or wire contract changed.

## Verification

Broker evaluation tests cover a project allow rule granting and not granting
external paths, folder-pattern coverage for other structured tools, and deny
precedence. Host tests cover the derivations (terminal prefix, shell and
flag-first exactness, one-time refusals, folder pattern derivation) and an
end-to-end run: `allow_always` on an external ask saves one project rule, and a
later session reads a different file in the same folder without a new
approval. CLI and GUI contract tests keep the folder and remember choices on
both surfaces. The applicable .NET matrix and tooling guards are recorded in
operations/verification.md.
