---
status: accepted
date: 2026-08-29
---

# Agent capability profiles (Standard / Full Access)

Ru exposes one generation mode: Agent. The Standard preset advertises bounded
workspace reads/writes and device tools; Full Access adds the terminal
bindings and broadens policy fallbacks. Filesystem mutations, external paths,
destructive actions, and terminal requests retain their trust and approval
gates in both presets.

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
  explicit commands produce a typed, readback-verified request before model
  selection.
- The bounded model selection loop allows at most ten sequential tool calls;
  approvals still gate mutations. The former Worker v1 conversational
  generation branch is retired; Worker v1 now carries only runtime/device
  adapters.
