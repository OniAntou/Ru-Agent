---
status: accepted
date: 2026-08-23
---

# Mode capability profiles (Chat / Agent / Full Access)

Ru replaces the single model-visible volume binding with three session
capability profiles. Chat is read-only workspace access (`files.list`,
`files.read`, automatic); Agent adds write/edit, notes, and two-tier terminal
work behind previewed approvals; Full Access advertises the same tool ids as
Agent but loosens execution policy — shell and run without cards,
outside-workspace paths, and network. Filesystem mutations (`files.write`,
`files.edit`) never lose their manual approval card, even in Full Access:
silent content mutation on disk stays the highest-trust action Ru can take.

Grants remain session-scoped. Every launch starts in Chat, Full Access
requires one explicit confirmation per app run, and no grant survives session
replacement or restart. Terminal networking changes from blanket rejection to
policy gating: the adapter rejects network use only when the active grant
lacks the `terminal.network` scope, which only Full Access confers.
Elevation, admin, registry, and service installation stay out of scope with
no adapters.

## Recorded limitations and decisions

- Network isolation in v1 is intent-level, not OS-enforced: the model-declared
  `network` flag is checked against the session grant's `terminal.network`
  scope before a process starts, but nothing at the operating-system layer
  prevents a spawned process from reaching the network. OS-level enforcement
  is deferred to a later revision.
- Deterministic volume intent in Chat is declined by design (product ruling):
  Chat is read-only, so explicit volume commands fail typed
  (`capability_unavailable`) with bounded copy that points the user to the
  Agent-mode toggle ("Chế độ Chat chỉ đọc dữ liệu. Bật Agent mode để em điều
  khiển âm lượng nha.").
- Model-facing tool-selection channel wiring is an immediate follow-up plan:
  the current release ships the policy, advertisement, and execution pipeline
  plus volume; wiring the remaining advertised tools into model selection is
  next.
