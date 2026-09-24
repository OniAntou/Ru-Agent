---
status: superseded
date: 2026-08-29
supersedes: superseded by session-scoped directory grants + terminal risk
  classifications (2026-09-04 plan). The preset picker is no longer exposed
  in ChatView or `agent run`; current permission model lives in
  `docs/architecture.md` and `docs/operations/agent-tools.md`.
superseded_by: ADR-0011 model autonomy and workspace trust boundary
  (`0011-model-autonomy-and-workspace-trust-boundary.md`, accepted
  2026-09-24).
---

# Agent capability profiles (Standard / Full Access)

> Current decision: [ADR 0011](0011-model-autonomy-and-workspace-trust-boundary.md)
> defines the canonical `plan` / `standard` / `full_access` matrix, explicit
> canonical-identity trust, and native multi-call behavior. The sections below
> preserve this ADR's superseded rationale and history.

Ru exposes one generation mode: Agent. The Worker v2 settings currently
advertise the same ten file/terminal bindings in Standard and Full Access.
Standard applies the stricter Broker analyzer/rule/approval decisions; Full
Access broadens policy fallbacks for well-formed calls. Workspace trust, hard
invariants, protected paths, and extended timeout thresholds remain
authoritative in both presets.

Grants remain session-scoped. Every launch starts in Agent Standard, Full
Access requires one explicit confirmation per run, and no grant survives
session replacement or restart. There is no Chat compatibility transport or
read-only generation profile. Elevation, admin, registry, and service
installation stay out of scope with no adapters. The picker was
retired in 2026-09-04; the current pending-approval model exposes
`external_directory_required`, `terminal_delete_requires_approval`,
`terminal_external_mutation_requires_approval`,
`terminal_timeout_requires_approval`, `web_approval_required`, and
`terminal_unknown_executable` as the ask reasons.

## Recorded limitations and decisions

- Network isolation is intent-level, not OS-enforced: the model-declared
  `network` flag is checked against the session grant's `terminal.network`
  scope before a process starts, but nothing at the operating-system layer
  prevents a spawned process from reaching the network. OS-level enforcement
  is deferred to the separate sandbox project.
- The bounded model selection loop allows at most 128 model steps and 256
  sequential tool calls per budget window (`AgentBudgetPolicy`); exhausting the
  window moves the run to `WaitingUser` and an explicit Continue opens a fresh
  window. Approvals still gate mutations. The former Worker v1 conversational
  generation branch and the device/runtime v1 adapters it later carried are
  both removed; the managed Worker serves `/worker/v2` only.

## Superseded history

The Standard / Full Access picker was a UI affordance that mapped to a
reusable rule. Session-scoped agent permissions removed the picker; the
chat composer now starts a run with the broker's effective Standard
profile and lets the Broker decide each request from the active session
grant plus terminal risk classification. Full Access is no longer a
user-selectable preset. See the 2026-09-04 implementation plan for the
new contract.
