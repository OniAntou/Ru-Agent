---
status: accepted
date: 2026-08-29
---

# Agent capability profiles (Standard / Full Access)

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
installation stay out of scope with no adapters.

## Recorded limitations and decisions

- Network isolation is intent-level, not OS-enforced: the model-declared
  `network` flag is checked against the session grant's `terminal.network`
  scope before a process starts, but nothing at the operating-system layer
  prevents a spawned process from reaching the network. OS-level enforcement
  is deferred to the separate sandbox project.
- Deterministic volume intent is available in Agent Standard and Full Access;
  explicit commands produce a typed, readback-verified `system.volume` request
  before model selection. `system.volume` is not a Worker v2 model binding.
- The bounded model selection loop allows at most ten sequential tool calls;
  approvals still gate mutations. The former Worker v1 conversational
  generation branch is retired; Worker v1 now carries only runtime/device
  adapters.
