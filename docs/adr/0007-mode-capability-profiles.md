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
