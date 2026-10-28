---
status: accepted
date: 2026-10-25
---

# Retire the legacy tool surface

## Context

The explicit coding tools replace the multiplexed file and terminal surface.
Keeping both surfaces increases contract and maintenance costs. The operator
requested removal of legacy execution and deletion of histories that used it.

## Decision

Supersede the legacy compatibility clauses in [ADR 0027](0027-pinned-coding-tool-profiles.md).
Only `coding_v2` (default), `patch_v2` and `coding_code_v1` remain supported.
Remove the public `write`, `edit` and `bash` definitions, manifests and dispatch
bindings. The Protocol catalog has 27 definitions; the Broker has 26 bindings
because the Host intercepts `ask_user`.

Every persisted provider selection must carry a supported `ru_tool_profile`.
A missing pin fails as `tool_profile_required`; a retired or unknown pin fails
as `tool_profile_invalid`. Validation precedes provider access. Do not infer or
rewrite a replacement surface during restart, recovery or import. Queries may
show a null label for unsupported metadata without authorizing execution.

Modern identities dispatch directly to shared file and terminal implementation
adapters through the existing Broker authority. Internal operation-family tags
and saved policy matchers remain so existing deny/ask rules keep constraining
their corresponding operations. Retaining those matchers never registers a
retired tool or widens a preset, grant, approval token or path rule.

Historical cleanup is an explicit operator action, not a startup migration.
Identify Ru-owned root runs by their persisted pins/execution evidence; delete
qualifying conversation histories through the existing session-deletion API.
That authority handles cancellation, grants, child artifacts and tombstones.
Do not delete unknown empty sessions, external CLI histories or credentials by
guess. Future unsupported imports fail closed rather than being auto-deleted.

## Consequences

Old executable transcripts cannot resume. New runs keep their pinned supported
inventory, provider and model. Shared adapters and policy-family tags are
implementation details, not a second public API. Tests cover rejected retired
IDs, invalid/missing pins, current operations and preserved policy decisions.
This changes no HTTP route, storage schema or OS execution boundary.
