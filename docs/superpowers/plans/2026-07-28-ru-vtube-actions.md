# Ru VTube Action Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Ru choose safe facial and body animations in VTube Studio without exposing control metadata in chat, TTS, memory, or approved examples.

**Architecture:** A pure parser removes a terminal `[[VTS:...]]` suffix and returns only allowlisted semantic actions. `VTSManager` maps them to the model's known safe hotkeys, throttles repetitions, and schedules safe reset hotkeys. The GUI stores, renders, and speaks the parser's clean reply before dispatching actions.

**Tech Stack:** Python 3.13, Tkinter, pytest, pyvts, VTube Studio Public API.

---

### Task 1: Parse and hide action metadata

**Files:**
- Create: `src/mini_gpt/vts_actions.py`
- Create: `tests/test_vts_actions.py`

- [ ] **Step 1: Write the failing parser tests**

```python
from mini_gpt.vts_actions import VtsActions, parse_vts_reply


def test_parse_vts_reply_removes_valid_suffix_and_returns_actions():
    assert parse_vts_reply("Hay quá! [[VTS:joy,raise_hand]]") == (
        "Hay quá!", VtsActions(facial="joy", body="raise_hand")
    )


def test_parse_vts_reply_hides_invalid_or_duplicate_suffix_without_actions():
    assert parse_vts_reply("Chào anh [[VTS:joy,joy,change_clothes]]") == (
        "Chào anh", VtsActions()
    )
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_vts_actions.py -q`

Expected: FAIL because `mini_gpt.vts_actions` does not exist.

- [ ] **Step 3: Implement the strict pure parser**

```python
@dataclass(frozen=True)
class VtsActions:
    facial: str | None = None
    body: str | None = None


def parse_vts_reply(reply: str) -> tuple[str, VtsActions]:
    # Strip a terminal [[VTS:...]] suffix.
    # Accept one unique facial action (joy/surprised/gentle) and one unique
    # body action (raise_hand/tail_up); otherwise return VtsActions().
```

Strip malformed terminal VTS suffixes too, so metadata never reaches a visible
or persistent boundary.

- [ ] **Step 4: Run the parser tests**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_vts_actions.py -q`

Expected: PASS.

### Task 2: Dispatch only approved VTube Studio actions

**Files:**
- Modify: `src/mini_gpt/vts_manager.py:1-105`
- Extend: `tests/test_vts_actions.py`

- [ ] **Step 1: Write failing dispatch-policy tests**

Use a fake VTS request object. Assert that `joy` resolves only to `星星眼`,
`raise_hand` resolves only to `手抬起`, unknown actions resolve to no request,
and no mapping contains clothes, hair, `嘴恢复`, or `LLM_MouthOpen`.

- [ ] **Step 2: Run the focused tests to verify failure**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_vts_actions.py -q`

Expected: FAIL because no safe action dispatcher exists.

- [ ] **Step 3: Implement safe action dispatch**

```python
ACTION_HOTKEYS = {
    "joy": ("星星眼", "眼睛恢复"),
    "surprised": ("惊讶", None),
    "gentle": ("闭眼", "眼睛恢复"),
    "raise_hand": ("手抬起", "手放下"),
    "tail_up": ("尾巴上竖", "尾巴下竖"),
}
```

Add `trigger_actions_sync(actions: VtsActions)`. Resolve names only through
this mapping, enforce per-action cooldowns with a lock and `time.monotonic`,
call existing hotkey dispatch for valid actions, and schedule reset hotkeys on
daemon `threading.Timer` instances. Missing hotkeys log warnings without
raising. Retain `LLM_Sway`; add no mouth API control.

- [ ] **Step 4: Run dispatch tests**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_vts_actions.py -q`

Expected: PASS.

### Task 3: Keep metadata out of every GUI output

**Files:**
- Modify: `src/mini_gpt/gui.py:20-45,700-735`
- Extend: `tests/test_gui.py`
- Modify: `src/mini_gpt/ru_style.py:1-37`

- [ ] **Step 1: Write failing GUI-boundary tests**

Use a fake `VTSManager` and a reply ending in `[[VTS:joy,tail_up]]`. Assert
that the stored `ChatTurn`, displayed reply, memory text, and TTS argument are
clean natural text, while the manager receives
`VtsActions(facial="joy", body="tail_up")`.

- [ ] **Step 2: Run the focused GUI test to verify failure**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_gui.py -q`

Expected: FAIL because the current GUI stores raw replies and parses arbitrary
asterisk-delimited hotkey names.

- [ ] **Step 3: Parse before every boundary**

At `_generation_finished`, call `parse_vts_reply(result)` before creating
`ChatTurn`. Use `clean_result` for transcript, memory, and `_play_tts`, then
call `self.vts_manager.trigger_actions_sync(actions)`. Remove the raw
asterisk-delimited hotkey path. Extend the system prompt with the exact terminal
suffix rule: at most one facial and one body action, only when useful, and
never describe metadata in prose.

- [ ] **Step 4: Run focused GUI and parser tests**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_vts_actions.py tests\test_gui.py -q`

Expected: PASS.

### Task 4: Verify and document the feature

**Files:**
- Modify: `README.md`
- Verify: `docs/superpowers/specs/2026-07-28-ru-vtube-actions-design.md`

- [ ] **Step 1: Document the VTube Studio setup**

Add a concise README section listing the five semantic actions, required
hotkeys, invisible metadata behavior, and that `VoiceVolume` remains the only
mouth/lip-sync input.

- [ ] **Step 2: Run complete checks**

Run: `& .\.venv\Scripts\python.exe -m pytest -q`

Run: `& .\.venv\Scripts\python.exe -m py_compile src\mini_gpt\vts_actions.py src\mini_gpt\vts_manager.py src\mini_gpt\gui.py`

Run: `git diff --check`

Expected: tests and syntax checks pass; changed files have no whitespace
errors.

- [ ] **Step 3: Perform a live VTube Studio smoke check**

With VTube Studio open and the GUI restarted, send a friendly prompt likely to
produce `joy` or `raise_hand`. Confirm no `[[VTS:` text reaches chat or TTS,
only the safe animation triggers, no appearance hotkey triggers, and
`VoiceVolume` lip-sync remains active.

- [ ] **Step 4: Commit only the feature files**

Stage `README.md`, the three feature modules, both focused test files, and the
spec/plan documents. Commit with `feat: add safe Ru VTube action controls`.
