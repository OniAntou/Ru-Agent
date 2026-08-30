# Ru Agent Session Rotation and Vietnamese Volume Semantics

Date: 2026-08-23
Status: Historical implementation record. Session lease rotation and the
Vietnamese volume distinction shipped; current behavior is in
[`docs/operations/agent-tools.md`](../../operations/agent-tools.md).

## Context

A live GUI acceptance run completed one verified `system.volume@1.0.0` action,
then rejected a later volume action with `session_conflict` before the Windows
adapter ran. The Worker had already resolved the second prompt into a typed
volume request. The two actions belonged to different chat session IDs.

The mismatch is architectural:

- `RuAgentBroker` intentionally owns one active Agent Session at a time.
- `AgentTurnCoordinator` caches grants in a dictionary keyed by chat session and
  tries to open a second Broker session without closing the first one.
- `BrokerSession` correctly rejects that second owner.

The deterministic Vietnamese parser also treats `tăng âm lượng lên 10` as a
relative increase of 10. In natural Vietnamese, `lên 10` names the target level;
`thêm 10` names a relative increase.

## Goals

- Allow verified tool execution to continue after the user changes chat
  sessions.
- Preserve the Broker's single-owner, fail-closed security boundary.
- Interpret `tăng âm lượng lên N` as an absolute target and `tăng âm lượng thêm
  N` as a relative increase.
- Lock both failures down with deterministic tests that never modify a real
  audio endpoint.

## Non-goals

- Do not let the Broker own multiple Agent Sessions concurrently.
- Do not persist Agent grants across process restarts.
- Do not infer a volume action from shorthand such as `tăng lên 10` when the
  current prompt contains no volume topic.
- Do not change Agent-mode UI behavior, approval policy, tool manifests, or any
  non-volume tool.
- Do not use native Windows audio during automated verification.

## Design

### One active Host lease

`AgentTurnCoordinator` will replace its per-session grant dictionary with one
active `AgentSessionGrant`. The existing semaphore remains the sole owner of
lease transitions.

For each tool request, the coordinator will:

1. Reuse the active grant only when its session ID matches, it has not expired,
   and it contains every scope required by the selected capability.
2. Otherwise, close the active Broker session before opening another one.
3. Clear the local active grant only after a successful close.
4. Validate that the returned grant contains every required scope before
   publishing it or allowing execution. If it is incomplete, close that new
   Broker session, retain no local lease, and fail with `scope_not_granted`.
5. Reuse or publish a replacement grant only after open and scope validation
   succeed. If the defensive close of an incomplete new grant itself fails,
   retain that grant only as an ownership record so the next request can retry
   cleanup; it must never authorize execution.
6. Emit `agent.session` only for a newly opened, validated grant.

If close or open fails, the action fails closed and the adapter is not invoked.
A failed close leaves the old local lease available for another bounded close
attempt. A successful close followed by a failed open leaves no active lease.

The Broker remains unchanged: a different session cannot take ownership until
the current owner closes. This preserves the current process-isolation and
single-owner contract instead of weakening it to hide the Host bug.

### Vietnamese volume grammar

The deterministic parser will separate target and delta wording:

- `tăng âm lượng lên 10` -> `{"operation":"set","level":10}`
- `tăng âm lượng lên mức 10` -> `{"operation":"set","level":10}`
- `tăng âm lượng thêm 10` -> `{"operation":"increase","amount":10}`

Existing range checks remain unchanged: absolute levels are 0-100 and relative
amounts are 1-50. Ambiguous, negated, hypothetical, quoted, and topic-free text
continues to avoid execution.

## Error behavior

- A Broker close/open transport error becomes the existing bounded failed tool
  outcome; Ru must not claim success.
- A stale or mismatched receipt remains rejected by the existing Worker
  validation.
- `session_conflict` remains a valid Broker defense. The Host prevents it during
  normal rotation rather than retrying around it or silently overriding the
  owner.
- Parser inputs outside the accepted grammar continue to request clarification
  or remain ordinary conversation, according to the existing safety rules.

## Verification

Add regression coverage at the real ownership seam:

- A stateful fake Broker enforces one active session. Two verified coordinator
  calls using different session IDs must produce
  `open A -> execute A -> close A -> open B -> execute B`.
- Two calls in the same valid session reuse one grant and do not close/reopen it.
- A failed close prevents opening or executing in the replacement session.
- `BrokerSession` tests retain the invariant that a second owner is rejected
  until the first owner closes.
- Parser tests distinguish `lên 10` from `thêm 10` and retain topic-free
  shorthand as non-action.

Run focused Python and .NET tests first, then the relevant full Python, Host,
Broker, WinUI contract, documentation, and route-matrix gates sequentially.
Automated success does not claim that a real Windows volume changed; the final
two-command GUI acceptance remains a user-run check.

## Documentation impact

Update the current operations documents to describe single-lease rotation and
the target-versus-delta wording. Historical superseded plans remain historical
and must not be rewritten as current behavior. No ADR is required because the
Broker ownership boundary is preserved rather than changed.
