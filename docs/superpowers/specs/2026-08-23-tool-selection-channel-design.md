# Design: Activate the model tool-selection channel

Date: 2026-08-23
Status: accepted (follow-up to mode-capability-profiles; amendment noted in ADR-0007)

## Background

The mode-capability-profiles branch shipped the full policy/advertisement/approval/execution pipeline for `files.*` / `terminal.*` bindings, but the model has no runtime path to invoke them: `_chat_handler.run_generation` only ever runs `phase="normal"`, `parse_tool_call` / `validate_selection` have zero production callers, and the selection prompt hardcodes volume-only. Product ruling: wire the channel now.

## Goals

- In every mode, the model can invoke its advertised tools through a structured, bounded loop.
- Selection output never leaks as visible chat text.
- Every call traverses the existing pipeline: validate → `CHAT_TOOL_REQUEST` → Host → Broker policy/approval → receipt.
- Text-only OpenAI-compatible providers keep working unchanged.

## Non-goals

- Provider-native function calling (`tools=` / native `tool_calls` deltas).
- Parallel/multiple tool calls in one model reply.
- Advertising Broker JSON schemas to the worker (bounded hints instead).

## Decisions (product rulings)

- Tool-call budget: **10** sequential calls per chat turn; budget exhaustion forces one final wrap-up generation with no tools.
- Chat mode participates: its advertised read tools are invokable through the same channel (reads need no cards).
- Full Access shell semantics already shipped cardless/unconfined under the `terminal.network` grant marker.

## Architecture

### 1. Advertisement gains `hint`

`BuildWorkerSettings` adds `"hint"` (string ≤200 chars, may be empty) to each
`model_tools` entry, built from a static host-side map keyed by tool id
(files.list/read/write/edit, terminal.run/shell, system.volume — short Vietnamese
usage shapes like `{"path":"ten-file-trong-workspace"}`). Worker
`parse_model_capabilities` accepts and bounds the field; unknown extra fields
stay rejected. Hints are prompt material only — Broker schemas remain internal.

### 2. Capability-driven selection prompt

`policy/style.py::ru_action_selection_prompt` is rewritten: lists each advertised
`id@version` plus its hint; instructs the model to answer EITHER with normal
Vietnamese prose OR with exactly one JSON object `{"tool_id", "tool_version",
"arguments"}` and nothing else; forbids multiple objects/markers; removes the
volume-only tuple validation entirely.

### 3. Phase loop in `run_generation` (budget 10)

```
for attempt in 0..9:
    generate(phase="normal" if first else "tool_result",
             action_capabilities=request.model_tools)
    text = accumulated output
    if text parses+validates as selection (parse_tool_call → validate_selection):
        publish CHAT_TOOL_REQUEST; await receipt (existing machinery)
        if capability_class == "native_mutation":
            render action outcome (existing truthful volume copy); DONE
        else:
            continue loop (next generation sees the outcome summary via
            agent_follow_up prompt)
    else:
        final prose answer; DONE
budget exhausted → one final generation with empty capabilities (wrap-up)
```

Streaming hold-back rule during any phase where `model_tools` is non-empty:
buffer silently until the first non-whitespace character; if it is `{`, keep
buffering the whole reply (selection candidate — never displayed live);
otherwise stream normally from that point.

Failed candidate (looks like JSON but fails parse/validation): discard the
text, emit fixed line "Em chưa gửi được lệnh nội bộ, anh thử lại nha." plus one
bounded diagnostic event `selection_invalid`; counts against budget.

Receipt failure/denial inside the loop: existing typed failure copy renders and
the loop continues (the model may correct itself within budget); denial copy
comes from the established error-code table.

### 4. Safety

Unchanged per-call pipeline: validation → publish → policy → approval cards →
receipt; 30 s deadline per call; cancellation/session-switch tears down the
waiting state; budget cap bounds runaway loops; hold-back prevents protocol
JSON from reaching the visible transcript or history.

## Testing

- Unit: hint parsing/bounds; generic selection prompt lists advertised ids and
  drops volume-only validation; hold-back stream strategy ({ vs prose); budget
  exhaustion wrap-up; failed-candidate line + diagnostic; loop continuation on
  tool-class receipts; volume native_mutation early-exit preserved.
- Integration (fake worker/host): Agent-mode scenario write→card→approve→
  follow-up answer end-to-end; Chat-mode files.read auto-executed then answered.
- Regression: all existing selection/receipt/prompt tests updated in place.

## Rollout

Single plan, four tasks: (1) hint plumbing host+worker parse; (2) generic
prompt rewrite; (3) run_generation loop + hold-back + failure line; (4) e2e
tests + docs touch-ups (agent-tools matrix note, ADR-0007 follow-up closure).
