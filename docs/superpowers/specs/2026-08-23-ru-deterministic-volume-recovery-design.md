# Ru Deterministic Volume Recovery

**Date:** 2026-08-23

**Status:** Historical implementation record. Deterministic volume parsing,
recovery, and truthful activity behavior shipped; current capability truth is
in [`docs/operations/agent-tools.md`](../../operations/agent-tools.md).

**Scope:** Deterministic volume intent, truthful failure continuity, and honest
Agent activity copy

## 1. Decision summary

Ru will stop asking the configured conversation model to produce the
`system.volume@1.0.0` request. The Runtime Worker will parse explicit volume
commands into one typed action deterministically, ask a deterministic
clarifying question when the command is ambiguous, and leave unrelated prompts
on the normal conversation path.

The existing trusted execution chain remains unchanged:

```text
WinUI chat
    -> ASP.NET Core Public Host
    -> typed Runtime Worker request
    -> deterministic system.volume intent
    -> Public Host session grant
    -> RuAgentBroker policy and adapter
    -> Core Audio readback receipt
    -> deterministic visible result
```

RuAgentBroker remains the only process allowed to mutate Windows volume. A
deterministic intent is not evidence of execution, and visible success remains
illegal until the Worker validates a matching `succeeded` receipt with
`verification_state: verified`.

## 2. Root cause and evidence

The configured Kilo model receives an ordinary streamed chat-completions
request. Action selection is requested only through system-prompt text; the
provider request does not use function calling, a JSON schema, or constrained
decoding. A model may therefore return natural prose or malformed JSON even
when the user gave an unambiguous volume command.

The Worker currently maps every pre-dispatch selection failure and every
post-dispatch action failure to the same sentence. The Public Host persists
only that visible sentence in conversation history. A later question such as
"sao vậy?" has no bounded action reason, so the model can invent an explanation
such as missing device permission.

The Agent surface also displays "Đang chờ Host cấp quyền" immediately when the
mode toggle is enabled, before any tool request exists. That copy describes an
operation that may never have started.

Live diagnosis established the failing boundary without invoking a native
action: the Broker health endpoint was ready, its authenticated hello advertised
one model-enabled `system.volume@1.0.0` capability with `system.write`, and the
affected turn emitted no `tool.requested`, adapter, verification, or receipt
event. The request therefore stopped in Worker selection before the Public Host,
Broker, or Core Audio adapter.

## 3. Approaches considered

### 3.1 Selected: deterministic-first volume intent

Parse the single model-enabled v1 capability with a small, fail-closed grammar.
Explicit commands become typed requests; ambiguous commands become clarification;
ordinary conversation stays ordinary conversation.

This removes provider compliance from the action control plane, adds no network
round trip, and preserves the Broker-owned execution and verification boundary.

### 3.2 Deferred: provider-native function calling or JSON schema

Provider-native structured output is the preferred future path when Ru exposes
several model-usable tools and every supported provider has a tested capability
contract. It is not selected for this repair because OpenAI-compatible gateways
and free models do not expose one uniform tools or response-format contract.

### 3.3 Rejected: prompt retry after malformed output

A second selection prompt would increase latency and still let model behavior
decide whether an explicit desktop command reaches the Broker. It would reduce
the failure rate without removing the root cause.

## 4. Goals

- Execute an unambiguous Vietnamese or English volume command without a model
  selection call.
- Preserve one typed Worker-to-Host-to-Broker path for normal chat and Agent
  mode.
- Ask before execution when a phrase can reasonably mean either an absolute
  level or a relative change.
- Render success and failure from validated typed data, not model prose.
- Explain the immediately previous action failure without invoking the model or
  exposing private diagnostics.
- Make Agent activity text describe only states that have actually occurred.
- Add bounded diagnostics for pre-dispatch intent outcomes while preserving the
  existing logging privacy policy.

## 5. Non-goals

- Enabling terminal, files, Web, connectors, application launch, or skills for
  the model.
- Adding a general natural-language tool planner.
- Moving Core Audio access out of RuAgentBroker.
- Treating deterministic, adapter, publish, or GUI Harness evidence as native
  Windows-volume acceptance.
- Persisting raw prompts, model selection output, tool arguments, volume values,
  device names, native errors, or credentials in diagnostics or action context.

## 6. Deterministic intent model

The volume parser returns one of three outcomes:

1. `not_action`: the prompt is not an executable volume command;
2. `clarification_required`: volume control is intended but the operation or
   quantity is not unambiguous;
3. `execute`: one validated `system.volume@1.0.0` request with exact arguments.

The parser is a pure function. It does not inspect provider state, open a
session, call the Broker, acquire an audio endpoint, or generate visible success
copy. Its output is validated again by the existing Worker and Broker schemas.

The implementation belongs in a focused generation-domain module rather than
expanding `worker/v1/handlers.py` or mixing natural-language parsing into the
Core Audio adapter.

## 7. Supported command semantics

The first release supports these deterministic meanings in Vietnamese and
English:

| User meaning | Typed arguments |
| --- | --- |
| Set or lower volume to an explicit level from 0 through 100 | `{"operation":"set","level":N}` |
| Increase volume by an explicit amount from 1 through 50 | `{"operation":"increase","amount":N}` |
| Decrease volume by an explicit amount from 1 through 50 | `{"operation":"decrease","amount":N}` |
| Mute | `{"operation":"mute"}` |
| Unmute | `{"operation":"unmute"}` |
| Read current volume | `{"operation":"get"}` |

Phrases equivalent to "xuống còn 2", "về 2", or "to 2 percent" mean absolute
`set`. Phrases equivalent to "giảm 2" or "decrease by 2" mean relative
`decrease`. A bare phrase such as "giảm âm lượng 2%" is ambiguous and must ask
whether the user means "giảm đi 2%" or "đặt còn 2%".

Capability questions such as "em chỉnh âm lượng được không?" do not execute a
tool. They receive a bounded capability answer or clarification. Negated,
hypothetical, quoted, or explanatory phrases do not execute. Values outside the
manifest ranges produce a deterministic range explanation and no tool request.

The parser normalizes Unicode case and whitespace but does not use fuzzy matching
that could turn unrelated prose into a desktop mutation. Unknown forms fail
closed as clarification or ordinary conversation.

## 8. Worker action flow and rendering

For each turn, the Worker evaluates the deterministic volume intent before
ordinary generation:

- `not_action`: call the configured model through the existing normal chat path;
- `clarification_required`: return deterministic natural Vietnamese copy and an
  action outcome without calling the model;
- `execute`: emit exactly one `chat.tool.request`, await one bounded receipt,
  validate correlation and proof, then render the result deterministically.

Agent mode does not force every ordinary prompt through a JSON-selection phase.
With v1's capability set, an unrelated Agent-mode prompt follows normal chat.
The generic structured-selection types remain available for a future
provider-native multi-tool design, but prompt-only JSON is not an execution path
for volume.

Verified volume output is parsed against the existing manifest shape:
`operation` is known, `level` is an integer from 0 through 100, and `muted` is a
boolean. The visible success sentence may use these values because they are the
Broker's readback result. They are not written to technical diagnostics.

Any failed, denied, cancelled, timed-out, mismatched, late, or unverified receipt
produces deterministic failure copy and never enters a model follow-up phase.

## 9. Truthful failure categories

The Worker maps stable internal codes to bounded public reasons. The mapping is
exhaustive for known volume failures and has one conservative fallback.

| Stable category or code | Public meaning |
| --- | --- |
| `volume_command_ambiguous` | The requested operation or quantity needs clarification. |
| `volume_value_out_of_range` | A level must be 0-100 or a relative amount must be 1-50. |
| `capability_unavailable`, `agent_unavailable` | Volume actions are unavailable in this runtime session. |
| `scope_not_granted` | This action session was not granted volume-control scope. |
| `invalid_arguments` | The typed volume request was rejected before native execution. |
| `unsupported_platform` | Native volume control is not available on this platform. |
| `default_endpoint_unavailable` | Windows has no accessible default playback endpoint. |
| `read_failed` | The current endpoint state could not be read. |
| `write_failed` | Windows rejected the requested write. |
| `readback_mismatch` | The write returned but readback did not confirm the target state. |
| `adapter_error` | The native adapter failed without a more specific safe code. |
| `cancelled`, `timed_out` | The action was cancelled or exceeded its deadline. |
| unknown bounded code | The action failed and Ru cannot safely claim a more specific reason. |

The UI and chat never translate an unknown failure into "permission denied".
Permission wording is legal only for an actual policy or scope-denial code.

## 10. One-turn action outcome continuity

`chat.done` gains an optional, strictly parsed action outcome containing only:

- schema version;
- tool ID and version;
- bounded terminal status;
- stable reason code or `null`.

It contains no arguments, requested or observed levels, output object, prompt,
reply, correlation ID, endpoint data, path, device identity, exception, or
credential.

The Public Host stores a failed or clarification outcome in an in-memory,
session-keyed one-shot store. The next turn atomically takes that context and
places it in an optional, strictly parsed `last_action_outcome` field on the
typed Worker chat request. If the prompt is exactly a short causal follow-up
such as "sao vậy?", "tại sao?", "vì sao?", "sao không được?", "why?", or
"why not?", the Worker returns the mapped explanation without calling the
model. Any unrelated turn ignores the supplied context; because the Host
already took it, the context is consumed and discarded. A new outcome replaces
the old one; session replacement, deletion, disconnect, and process exit clear
it.

The canonical visible failure sentence remains in ordinary conversation history.
The structured one-shot context is not persisted to the session JSON and is not
included in memory extraction, reviewed examples, TTS metadata, or VTS metadata.

## 11. Honest Agent activity states

The Agent surface uses user-facing states rather than internal host terminology:

| Trigger | Visible state |
| --- | --- |
| Agent toggle enabled, no request | `Agent mode đã bật` |
| Actual `agent.activity` with `requested` | `Đang chuẩn bị thao tác…` |
| Matching `agent.session` grant | `Đang thực hiện thao tác đã cấp quyền…` |
| Verified terminal result | `Thao tác đã được xác minh` |
| Failed terminal result | `Thao tác thất bại: <lý do an toàn>` |

"Đang chờ Host cấp quyền" is removed from production code, tests, and current
documentation. Enabling Agent mode does not imply a grant exists. Normal-chat
volume actions may show transient bounded activity while leaving the Agent-mode
toggle off. Session replacement and disconnect clear every pending state; stale
events for another session are ignored.

## 12. Diagnostics and privacy

Add `tool.intent.resolved`, `tool.intent.clarification`, and
`tool.intent.rejected` as bounded Worker diagnostic codes. No event is emitted
for `not_action`. Allowed fields remain event code, fixed tool ID/version,
terminal or selection status, stable error code, duration, and opaque
correlation ID when one already exists. Diagnostics must not include prompt
text, parser captures, numbers, model output, request arguments, receipt output,
visible copy, or native exception text.

The existing Host and Broker trail remains authoritative after
`tool.requested`: policy, adapter start/completion, verification, and receipt
delivery continue to use their current fixed event codes. Absence of
`tool.requested` therefore remains proof that no Broker action began.

## 13. Testing and acceptance

Implementation follows red-green-refactor at each seam.

### 13.1 Parser contract

Table-driven tests cover Vietnamese and English absolute set, relative
increase/decrease, mute, unmute, and get commands. Negative tests cover
ambiguity, negation, capability questions, quoted commands, ordinary audio
discussion, missing values, decimal or boolean-like values, repeated commands,
conflicting operations, and every range boundary.

### 13.2 Worker integration

Tests prove that an explicit command emits exactly one typed request before any
model call. A verified fake receipt produces deterministic success from validated
output. Every non-success receipt produces its mapped failure and no model call.
Ambiguity produces clarification and no request. Ordinary chat still invokes the
model normally in both normal and Agent mode.

### 13.3 Host and session continuity

Typed protocol tests reject malformed or oversized action outcomes. Session
tests prove one-shot storage, matching-session isolation, replacement, unrelated
turn consumption, new-session cleanup, and deterministic causal follow-up.
Neither persisted session history nor memory records contain the structured
outcome.

### 13.4 GUI state contract

WinUI tests cover idle, requested, granted, verified, failed, disabled,
disconnected, and stale-session states. Source-contract tests require the old
Host-waiting copy to be absent. The deterministic GUI Harness continues to use
synthetic boundaries and must not open a physical audio device.

### 13.5 Release gates

Run the full Python suite, Broker tests, connector tests, Public Host tests,
WinUI tests, GUI Harness build, 41-HTTP/2-WebSocket route matrix, deterministic
contract Harness, and publish script sequentially. Native volume and live GUI
acceptance remain explicit user-run checks and must be reported as pending until
performed.

## 14. Documentation impact

Update these current documents with the implemented behavior:

- `docs/operations/agent-tools.md` for deterministic-first selection and
  truthful failure continuity;
- `docs/operations/runtime-behavior.md` for the action and Agent UI state flow;
- `docs/operations/logging.md` for bounded pre-dispatch diagnostics;
- `docs/operations/verification.md` for parser, continuity, and GUI gates;
- `README.md` only if its user-facing Harness description or command list changes.

The implemented Action Execution Kernel v1 design remains authoritative for
Broker ownership and verified receipts. This document narrows and repairs its
prompt-only volume-selection assumption; it does not supersede the one-path
execution architecture.

## 15. Acceptance criteria

The repair is complete only when all of the following are true:

1. "Giảm âm lượng của anh xuống còn 2" produces one `set` request with level 2
   without a model selection call.
2. The same explicit command behaves identically in normal chat and Agent mode.
3. Ambiguous, negated, quoted, conflicting, repeated, or out-of-range commands
   cannot reach the Public Host tool handler.
4. Ordinary Agent-mode conversation does not enter prompt-only JSON selection.
5. Only a correlated `succeeded` plus `verified` receipt can produce success.
6. Known failures render their truthful mapped reason; unknown failures use the
   conservative fallback and never invent a permission problem.
7. The immediate causal follow-up consumes one bounded action outcome and does
   not invoke the model.
8. Action outcome metadata is in-memory, one-shot, session-isolated, bounded,
   and absent from history, memory, TTS, VTS, examples, and diagnostics.
9. Agent UI copy reflects idle, requested, granted, and terminal states and
   contains no "Đang chờ Host cấp quyền" string.
10. Automated gates pass on the final commit, while native Windows-volume and
    live GUI acceptance remain explicitly unclaimed.
