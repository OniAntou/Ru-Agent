# Ru volume tool vertical slice

Date: 2026-08-22  
Status: Proposed — awaiting user review

## 1. Context

The current Python runtime can emit legacy `[VOLUME: ...]` markers, but the
adapter only presses Windows volume keys a fixed number of times. It cannot
read the current level or set an absolute level, and the orchestration path
does not use the adapter result to prevent a false success message.

Agent mode has a separate typed-tool path, but `system.volume` is not in the
Broker catalog or session scope. Agent generation disables legacy markers
without supplying a volume tool schema, so a volume request cannot reach the
Broker.

This slice uses volume as the first end-to-end tool contract. It also creates a
repeatable smoke-test boundary for the other registered tools without silently
expanding this change into a general tool implementation project.

## 2. Goals

- Let Ru read, set, increase, decrease, mute, and unmute the Windows default
  audio endpoint.
- Interpret “hạ xuống còn 2” as setting the endpoint to exactly 2 percent.
- Give normal chat and Agent mode the same operation semantics and verified
  result shape.
- Verify the post-action state before reporting success.
- Return a truthful bounded failure when Windows audio is unavailable or the
  readback does not match the requested state.
- Add deterministic tests for the normal marker path, the structured Agent
  path, and the Host/Broker policy boundary.
- Add a non-destructive smoke matrix for the existing registered tools.

## 3. Non-goals

- No per-application mixer control, output-device selection, audio routing, or
  microphone gain control.
- No PowerShell, shell, simulated keypress, or admin fallback for volume.
- No automatic execution of external connectors, web actions, or arbitrary
  terminal commands as part of this slice.
- No GUI redesign or changes to the unrelated dirty UI work.
- No claim that deterministic tests replace the user's live speaker/device
  acceptance test.

## 4. Versioned operation contract

The canonical identity is `system.volume@1.0.0`.

```json
{
  "tool_id": "system.volume",
  "tool_version": "1.0.0",
  "arguments": {
    "operation": "get | set | increase | decrease | mute | unmute",
    "level": 0,
    "amount": 4
  }
}
```

Validation rules:

- `operation` is required and must be one of the six listed values.
- `set` requires an integer `level` from 0 through 100.
- `increase` and `decrease` accept an integer `amount` from 1 through 50.
  When omitted by a legacy request, the compatibility default is 4 percentage
  points, matching the existing two-keypress behavior.
- `get`, `mute`, and `unmute` do not accept `level` or `amount`.
- Unknown fields, out-of-range values, and malformed JSON are rejected before
  any device operation.

A successful result is bounded and has this shape:

```json
{
  "operation": "set",
  "level": 2,
  "muted": false,
  "verified": true
}
```

The adapter reads the default render endpoint before and after a write. The
reported `level` is the rounded 0–100 scalar from the endpoint. A write is not
successful unless the requested level or mute state is confirmed by readback.

## 5. Runtime architecture

### 5.1 Shared semantics, bounded adapters

The Python runtime and the C# Broker use separate native adapters behind the
same operation and result contract. Both adapters target the Windows Core
Audio default render endpoint directly; neither invokes a shell or synthetic
keyboard input. Each adapter is injectable in tests so CI never changes the
machine's real volume.

The adapter boundary owns device access, clamping, readback, and low-level
errors. The orchestration boundary owns marker removal and truthful response
rendering. The Broker boundary owns tool identity, scope, and bounded safe
summaries.

### 5.2 Normal chat

Normal chat keeps a compatibility marker while the public chat contract is
unchanged. The prompt documents these forms:

```text
[VOLUME: get]
[VOLUME: set 2]
[VOLUME: increase 5]
[VOLUME: decrease 5]
[VOLUME: mute]
[VOLUME: unmute]
```

Legacy `[VOLUME: up]` and `[VOLUME: down]` normalize to `increase` and
`decrease` with the four-point compatibility default. The parser produces the
canonical request rather than passing free-form text to the adapter.

The marker executor removes internal metadata from displayed text. If the
adapter fails or readback is inconsistent, the renderer discards any model
success claim and emits a short truthful failure. A successful action may keep
the model's natural reply, but its outcome is based on the verified adapter
result rather than the model's assertion.

### 5.3 Agent mode

Agent generation receives a structured-tool instruction that includes the
`system.volume@1.0.0` identity, operation schema, and JSON-only output shape.
The worker parses the existing strict tool-call object and emits the normal
`chat.tool.request` envelope.

The Broker registers `system.volume@1.0.0` as a reversible desktop tool with
the `system.write` scope. Agent session grants include `system.write`, and the
Host maps `system.volume` to that scope. The operation does not require a
one-time approval because it is local and reversible. The Broker returns only
the bounded result and safe summary to the Host/Worker.

For a failed tool result, the Worker returns a deterministic truthful failure
instead of allowing a continuation turn to claim that the action succeeded.
For a verified success, the continuation receives the bounded result so Ru can
reply naturally with the actual level.

## 6. Error and privacy policy

- Missing default endpoint, unsupported platform, COM/native failure, invalid
  request, and readback mismatch are distinct internal error categories but
  produce bounded user-facing summaries.
- Raw COM errors, device names, arguments, credentials, and native details do
  not enter chat, TTS, memory, or activity summaries.
- A failed write has no success response and does not update long-term memory
  as if the action happened.
- The tool remains session-scoped and expires with the existing Agent grant.

## 7. Verification plan

Tests are written before production changes and run in red/green cycles.

Python coverage:

- parse every canonical marker and legacy `up/down` compatibility marker;
- reject malformed and out-of-range requests;
- verify set/get/relative/mute behavior with a fake Core Audio adapter;
- verify readback mismatch returns failure;
- verify a failed normal-chat action cannot leak a success claim.

C# coverage:

- register and validate the Broker manifest and `system.write` scope;
- validate dispatch arguments and bounded result shape;
- verify the Host scope mapping and session grant;
- verify the Agent tool request/result loop with a fake adapter;
- verify policy denial and expiry without touching a real device.

Integration coverage:

- deterministic normal chat marker execution;
- deterministic Agent Worker → Host → Broker → Worker continuation;
- non-destructive smoke matrix for current read-only and workspace tools;
- existing Python and .NET suites run sequentially on Windows.

Manual acceptance remains explicit: with the Ru GUI running and a known audio
device selected, ask Ru to report the level, set it to 2, increase it, mute,
unmute, and confirm the displayed Windows level changes accordingly.

## 8. Documentation updates

After implementation, update `docs/operations/agent-tools.md` to list
`system.volume` in the first usable tool set and record its reversible
`system.write` policy. Update the design/verification references only where
they describe the compatibility marker as the authoritative path.

The existing unrelated dirty files remain outside this slice. Only the new
spec is committed at the design stage; implementation commits will stage only
files belonging to this feature.

## 9. Acceptance criteria

The slice is complete when all of the following are true:

1. Normal chat can set an exact level and reports the verified level.
2. Agent mode can issue and complete `system.volume@1.0.0` without a marker.
3. A failed or unverifiable action cannot be rendered as successful.
4. No real device is modified by automated tests.
5. Targeted and full deterministic suites pass with fresh output.
6. The current docs describe the shipped Agent volume capability accurately.
