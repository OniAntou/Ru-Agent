# Tool-Selection Channel Implementation Plan

> **Status:** Historical implementation record. The selection loop shipped and
> was folded into the Agent-only prompt contract; current behavior is in
> `docs/operations/agent-tools.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the model actually invoke its advertised tools via a bounded prompt-protocol selection loop (budget 10) wired through the existing `CHAT_TOOL_REQUEST`/receipt pipeline.

**Architecture:** Worker-side phase loop in `run_generation` reuses the dormant machinery (`parse_tool_call`, `validate_selection`, `wait_for_tool_result`, publisher); advertisement gains bounded `hint` strings; the selection prompt becomes capability-driven; streaming holds back `{`-leading replies so protocol JSON never reaches the transcript. Providers stay text-only.

**Tech Stack:** Python 3.13 worker (pytest), .NET 10 Host (xUnit) for hint plumbing.

## Global Constraints

- No public contract changes; `chat.tool.request/result` envelopes unchanged except optional `hint` INSIDE worker settings only.
- Selection JSON must never be written to visible chat/history/diagnostics; failure line is fixed copy `"Em chưa gửi được lệnh nội bộ, anh thử lại nha."`.
- Budget = 10 tool calls per turn; deadline 30s per call (existing).
- Volume `native_mutation` early-exit rendering preserved byte-for-byte.
- Suites green each task: pytest -q; RuBackend.Tests when host files touched.
- Conventional commits ≤72 chars.

---

### Task 1: `hint` plumbing (Host emit + worker parse)

**Files:**
- Modify: `backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs` (`BuildWorkerSettings` model_tools block)
- Modify: `src/ru_runtime/worker/v1/action_loop.py` (`parse_model_capabilities`, `ModelCapability`)
- Test: `backend-dotnet/RuBackend.Tests/AgentModeProfileTests.cs` (extend), `tests/test_worker_action_loop.py` (extend)

**Interfaces:**
- Produces: `ModelCapability.hint: str`; wire entry key `"hint"` optional string ≤200 (longer → truncated host-side; non-string → rejected `invalid_model_tools`).

- [ ] **Step 1: Failing tests**

Python:
```python
def test_model_capabilities_accept_and_bound_hint():
    caps = parse_model_capabilities([
        {"tool_id": "files.read", "tool_version": "1.0.0",
         "scopes": ["workspace.read"], "availability": "executable",
         "model_enabled": True, "capability_class": "tool",
         "verification_required": False,
         "hint": "h" * 500},
    ])
    assert caps[0].hint == "h" * 200
```
C# (AgentModeProfileTests): assert agent-mode settings entries contain `hint` non-empty for files.read and absent-or-empty for unknown ids not in the static map.

- [ ] **Step 2: Red** both suites.
- [ ] **Step 3: Implement** — C# static map:
```csharp
private static readonly Dictionary<string, string> ToolHints = new()
{
    ["files.list"] = """{"path":"thu-muc-trong-workspace"}""",
    ["files.read"] = """{"path":"ten-file-trong-workspace"}""",
    ["files.write"] = """{"path":"...","content":"noi-dung-moi"}""",
    ["files.edit"] = """{"path":"...","old_string":"...","new_string":"..."}""",
    ["terminal.run"] = """{"command":"git","args":["status"]}""",
    ["terminal.shell"] = """{"script":"lenh-shell","network":false}""",
    ["system.volume"] = """{"operation":"set","level":30}""",
};
```
Emit `"hint"` (truncate 200) when id is in map. Python: add `hint` field to ModelCapability (default ""), parse `_bounded_text(hint, 200) or ""`, allow missing key.
- [ ] **Step 4: Green** both suites. **Step 5:** Commit `feat: bounded tool hints in capability advertisement`

---

### Task 2: Capability-driven selection prompt

**Files:**
- Modify: `src/ru_runtime/policy/style.py::ru_action_selection_prompt`
- Test: `tests/test_ru_style.py`

- [ ] **Step 1: Failing tests** — rewrite existing volume-only assertions:
```python
def test_action_selection_prompt_lists_every_advertised_capability():
    prompt = ru_action_selection_prompt((
        ModelCapability(tool_id="files.read", tool_version="1.0.0",
                        scopes=(), availability="executable",
                        model_enabled=True, capability_class="tool",
                        verification_required=False, hint='{"path":"..."}'),
        ModelCapability(tool_id="system.volume", tool_version="1.0.0",
                        scopes=(), availability="native_pending",
                        model_enabled=True, capability_class="native_mutation",
                        verification_required=True, hint=""),
    ))
    assert '"files.read"' in prompt and '"system.volume"' in prompt
    assert '{"path":"..."}' in prompt

def test_action_selection_prompt_no_longer_rejects_non_volume():
    prompt = ru_action_selection_prompt((_tool_cap("files.edit"),))
    assert "unsupported_model_capability" not in prompt
```
- [ ] **Step 2: Red. Step 3: Implement** — replace body: enumerate descriptors
(`f'- {c.tool_id}@{c.tool_version}: {c.hint}'`), rules paragraph:
reply EITHER Vietnamese prose OR exactly one JSON object with exactly
`tool_id`,`tool_version`,`arguments`; never multiple objects; never markers;
no prose around the object when selecting. Delete the volume tuple check +
`unsupported_model_capability` raise. Keep empty-capability variant returning a
prompt that can never select (existing test keeps passing).
- [ ] **Step 4: Green** pytest full. **Step 5:** Commit `feat(worker): capability-driven selection prompt`

---

### Task 3: Selection loop + streaming hold-back

**Files:**
- Modify: `src/ru_runtime/worker/v1/handlers.py` (`run_generation` ~L528, new helpers)
- Test: `tests/test_worker_runtime_handlers.py` (extend)

**Interfaces:**
- Consumes: `parse_tool_call`, `validate_selection(text, request.model_tools)`,
  `publish_chat_tool_request(...)` (extract from the volume branch's inline
  publish at handlers.py:434-448 into a helper reused by both),
  `wait_for_tool_result`, `render_action_*`, `_ACTION_DEADLINE_MS`.
- Produces: constants `MAX_TOOL_CALLS_PER_TURN = 10`,
  `SELECTION_INVALID_COPY = "Em chưa gửi được lệnh nội bộ, anh thử lại nha."`.

- [ ] **Step 1: Failing tests** (fake generation + fake queue, following
`test_explicit_volume_command_emits_one_request_without_model_generation` patterns):
```python
def test_agent_selection_runs_tool_then_follow_up_answer():
    # gen#1 returns a valid files.read selection JSON; receipt succeeded with
    # output {"text":"hello"}; gen#2 returns "Xin chào!"
    ... assert final assistant text == "Xin chào!" and one CHAT_TOOL_REQUEST
       published with tool_id files.read and NO raw JSON in streamed text

def test_budget_exhaustion_forces_wrapup_without_tools():
    # generation always returns a selection for 10 calls; call 11 must be
    # generated with action_capabilities=() and its text becomes final answer

def test_broken_json_candidate_is_suppressed_with_fixed_copy():
    # gen returns "{not json"; assert visible text == SELECTION_INVALID_COPY,
    # diagnostic event selection_invalid emitted once, nothing published

def test_holdback_streams_prose_but_buffers_braced_reply():
    # delta-level assertion: prose deltas forwarded live; braced reply emits none
```
- [ ] **Step 2: Red.**
- [ ] **Step 3: Implement**
  - Extract `publish_chat_tool_request(request, selection)` helper from the
    volume branch; volume branch refactored to use it (behavior identical).
  - Loop skeleton replacing single call at :528-535:

```python
remaining = MAX_TOOL_CALLS_PER_TURN
while True:
    phase = "normal" if first_pass else "tool_result"
    buffered = _StreamingHoldback(enabled=bool(request.model_tools))
    text = generate_once(phase=phase, stream=buffered.forward, ...)
    candidate_text = buffered.finish()   # returns held text if withheld
    selection = try_parse_selection(candidate_text, request.model_tools)
    if selection is None:
        final_text = candidate_text
        break
    if remaining <= 0:
        text = generate_once(phase="tool_result", action_capabilities=(), ...)
        final_text = text; break
    remaining -= 1
    publish_chat_tool_request(request, selection)
    outcome = wait_for_tool_result(...)  # existing
    if selection.capability_class == "native_mutation":
        render volume-style outcome; return
    record outcome summary for next agent_follow_up prompt
else-path: on parse-failure of a braced candidate ->
    emit diagnostic selection_invalid; final_text = SELECTION_INVALID_COPY; break
```
  - `_StreamingHoldback`: forwards deltas after first non-ws char unless it is
    `{`, then suppresses everything (internal buffer); `finish()` returns full
    accumulated text either way. Prose path must remain byte-identical to today
    (regression test `test_ordinary_normal_chat_streams_without_entering_selection`
    stays green untouched).
  - Follow-up prompt input: append bounded outcome summary (status + safe fields,
    ≤512 chars) via the existing `agent_follow_up` system prompt channel.
- [ ] **Step 4: Green** pytest full (~800).
- [ ] **Step 5:** Commit `feat(worker): bounded model tool-selection loop`

---

### Task 4: E2E scenarios + docs closure

**Files:**
- Test: `tests/test_worker_runtime_handlers.py` (two e2e-flavored scenarios), extend harness fixture tests if cheap
- Modify: `docs/operations/agent-tools.md` (selection-channel note), `docs/adr/0007-mode-capability-profiles.md` (close the follow-up note)

- [ ] **Step 1: Failing tests**
```python
def test_agent_mode_write_card_approve_then_edited_file_follow_up(): ...
    # full pipeline: selection files.write -> approval required event ->
    # approve -> receipt -> follow-up mentions completion; card preview asserted
def test_chat_mode_reads_workspace_file_without_cards(): ...
    # selection files.read -> auto receipt (read class) -> answer quotes content
```
- [ ] **Step 2: Red. Step 3:** implement scenario fixtures reusing existing
approval/receipt fakes. **Step 4:** green all suites (pytest + RuBackend untouched-check).
Docs: one sentence each.
- [ ] **Step 5:** Commit `test: end-to-end selection scenarios and docs closure`
