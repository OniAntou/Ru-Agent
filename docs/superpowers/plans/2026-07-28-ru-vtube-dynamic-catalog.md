# Ru Dynamic VTube Catalog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Ru invoke every hotkey and exposed Live2D expression of the currently loaded VTube Studio model through invisible, model-scoped codes.

**Architecture:** A pure catalog module assigns deterministic `vts_XX` and `expr_XX` codes to a model API snapshot. `VTSManager` reads the current model plus its hotkeys/expressions before generation and revalidates it before dispatch. The GUI supplies the catalog to inference, strips the suffix, and passes only validated IDs/files to VTube Studio.

**Tech Stack:** Python 3.13, Tkinter, pytest, pyvts, VTube Studio Public API.

**Status (2026-07-28):** Implemented and validated with the focused VTube
catalog, manager, fallback-action, and GUI tests. The unchecked boxes preserve
the original execution sequence; this plan is retained as implementation
history rather than a pending work queue.

---

### Task 1: Model-scoped catalog and parser

**Files:**
- Create: `src/mini_gpt/vts_catalog.py`
- Create: `tests/test_vts_catalog.py`
- Modify: `src/mini_gpt/vts_actions.py`

- [ ] **Step 1: Write failing catalog tests**

Build a synthetic model with two hotkeys and two expressions. Assert that the
catalog retains its `model_id`, sorts entries, assigns `vts_01`, `vts_02`,
`expr_01`, and `expr_02`, and excludes non-`.exp3.json` files. Assert that a
terminal `[[VTS:vts_01,expr_02]]` becomes clean text plus those entries, while
empty, malformed, repeated, and unknown codes produce no actions.

- [ ] **Step 2: Run the test to verify it fails**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_vts_catalog.py -q`

Expected: FAIL because `mini_gpt.vts_catalog` does not exist.

- [ ] **Step 3: Implement immutable catalog data**

```python
@dataclass(frozen=True)
class VtsCatalogEntry:
    code: str
    kind: Literal["hotkey", "expression"]
    label: str
    identifier: str


@dataclass(frozen=True)
class VtsCatalog:
    model_id: str
    model_name: str
    entries: tuple[VtsCatalogEntry, ...]
```

Implement `build_catalog`, `catalog_instruction`, and `parse_catalog_reply`.
The parser always strips a terminal VTS suffix but resolves actions only when
all unique codes belong to its supplied catalog. Preserve the existing local
Vietnamese safe-command fallback.

- [ ] **Step 4: Run catalog tests**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_vts_catalog.py -q`

Expected: PASS.

### Task 2: Snapshot and dispatch the current VTube Studio model

**Files:**
- Modify: `src/mini_gpt/vts_manager.py`
- Extend: `tests/test_vts_catalog.py`

- [ ] **Step 1: Write failing manager tests**

With a fake authenticated VTS client, return `CurrentModelResponse`,
`HotkeysInCurrentModelResponse`, and `ExpressionStateResponse`. Assert that a
matching snapshot dispatches hotkeys by `hotkeyID` and expressions by
`.exp3.json` filename; a changed `modelID` sends no request. Assert that only
manager-activated expressions are later deactivated.

- [ ] **Step 2: Run the test to verify it fails**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_vts_catalog.py -q`

Expected: FAIL because the manager has no catalog refresh or catalog dispatch.

- [ ] **Step 3: Implement raw VTS catalog requests**

Use `self.vts.vts_request.BaseRequest()` for `CurrentModelRequest`,
`HotkeysInCurrentModelRequest`, and `ExpressionStateRequest` with
`{"details": False, "expressionFile": ""}`. Add synchronous wrappers
`current_catalog_sync()` and `trigger_catalog_actions_sync(catalog, codes)`.
Before dispatch, refresh again and require equal `model_id`; use
`HotkeyTriggerRequest` for hotkeys and `ExpressionActivationRequest` with a
0.25-second fade for expressions. Track only manager-activated expressions and
deactivate them via daemon timers. Leave `VoiceVolume` and no-mouth-API rules
unchanged.

- [ ] **Step 4: Run manager/catalog tests**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_vts_catalog.py -q`

Expected: PASS.

### Task 3: Feed and consume the dynamic catalog in the GUI

**Files:**
- Modify: `src/mini_gpt/gui.py`
- Extend: `tests/test_gui.py`

- [ ] **Step 1: Write failing GUI tests**

Use a fake manager with a two-entry catalog. Assert that `_generate_worker()`
passes `catalog_instruction(catalog)` to inference and passes the same catalog
to completion. For a response ending in `[[VTS:vts_01,expr_01]]`, assert chat,
TTS, memory, and saved responses contain only natural text while the manager
receives the original catalog and both private codes.

- [ ] **Step 2: Run the test to verify it fails**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_gui.py -q`

Expected: FAIL because inference currently has no current-model catalog.

- [ ] **Step 3: Thread catalog state through one generation**

Extend `ModelRunner.generate_reply()` with optional `vts_instruction: str =
""`, appending it only to the runtime system prompt. In `_generate_worker()`,
fetch a catalog before inference and retain it through `_generation_finished()`.
Parse dynamic metadata before creating `ChatTurn`, then dispatch catalog codes.
Retain direct local command fallback for the legacy safe gestures.

- [ ] **Step 4: Run focused GUI and catalog tests**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_vts_catalog.py tests\test_gui.py -q`

Expected: PASS.

### Task 4: Documentation and live checks

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-07-28-ru-vtube-actions-design.md`
- Verify: `docs/superpowers/specs/2026-07-28-ru-vtube-all-hotkey-control-design.md`

- [ ] **Step 1: Update documentation**

Replace the fixed action table with the dynamic hotkey/expression catalog,
model-switch behavior, invisible metadata boundary, and the limitation that
animations outside hotkeys and expressions remain unavailable.

- [ ] **Step 2: Run regressions**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_vts_catalog.py tests\test_vts_actions.py tests\test_gui.py -q`

Run: `& .\.venv\Scripts\python.exe -m py_compile src\mini_gpt\vts_catalog.py src\mini_gpt\vts_actions.py src\mini_gpt\vts_manager.py src\mini_gpt\gui.py`

Run `git diff --check` with only the feature paths.

- [ ] **Step 3: Perform a live smoke test**

Restart Ru with VTube Studio open. Confirm a known hotkey and a known unbound
expression can run without a visible code. Change to another model and confirm
old-model codes do not dispatch; lip-sync through `VoiceVolume` must continue.

- [ ] **Step 4: Commit feature-owned files only**

Stage the catalog module, changed GUI/VTS/action modules, focused tests,
README, updated specs, and this plan. Do not stage unrelated dirty files.
Commit message: `feat: add dynamic VTube action catalog`.
