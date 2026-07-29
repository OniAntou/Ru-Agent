# Ru VTube Balanced Motion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce Ru's speaking sway from the current strong motion while preserving gentle movement and adding safe, occasional model-aware micro-actions.

**Architecture:** Put deterministic sway math, cooldown timing, and safe catalog filtering in a small pure motion module. `VTSManager` owns the persistent async loop and VTube Studio requests, while `RuApp` uses an explicit speaking-state method so every speech session resets the scheduler consistently. Semantic LLM actions remain on their existing catalog path and share a dispatch guard with automatic actions.

**Tech Stack:** Python 3.13, `asyncio`, `threading`, `random.Random`, `pyvts`, pytest, Tkinter GUI.

---

### Task 1: Add pure motion policy and tests

**Files:**
- Create: `src/mini_gpt/vts_motion.py`
- Create: `tests/test_vts_motion.py`

- [ ] **Step 1: Write the failing tests**

Add public tests for the motion policy:

```python
from mini_gpt.vts_motion import (
    IDLE_SWAY_AMPLITUDE,
    SPEAKING_SWAY_AMPLITUDE,
    MicroActionScheduler,
    is_safe_micro_action,
    sway_value,
)


def test_speaking_sway_is_smaller_than_idle_baseline_and_old_peak():
    assert SPEAKING_SWAY_AMPLITUDE == 8.0
    assert IDLE_SWAY_AMPLITUDE == 5.0
    assert abs(sway_value(1.0, speaking=True)) <= 8.0
    assert abs(sway_value(1.0, speaking=False)) <= 5.0


def test_scheduler_waits_for_a_randomized_interval_and_resets_on_new_session():
    scheduler = MicroActionScheduler(random_source=FakeRandom([20.0, 14.0]))
    assert scheduler.update(100.0, speaking=True) is False
    assert scheduler.update(119.9, speaking=True) is False
    assert scheduler.update(120.0, speaking=True) is True
    scheduler.update(121.0, speaking=False)
    assert scheduler.update(122.0, speaking=True) is False
    assert scheduler.next_due_at == 142.0


def test_micro_action_filter_excludes_pout_blush_clothing_and_unknown_motion():
    assert is_safe_micro_action("Blink", "blink.motion3.json") is True
    assert is_safe_micro_action("尾巴上竖", "tail-up.motion3.json") is True
    assert is_safe_micro_action("^", "表情嘴-^.motion3.json") is False
    assert is_safe_micro_action("脸红启用", "blush.motion3.json") is False
    assert is_safe_micro_action("外衣启用", "clothes.motion3.json") is False
    assert is_safe_micro_action("Mystery", "unknown.motion3.json") is False
```

Define the deterministic random source used by the scheduler test:

```python
class FakeRandom:
    def __init__(self, values):
        self.values = iter(values)

    def uniform(self, _minimum, _maximum):
        return next(self.values)
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_vts_motion.py -q
```

Expected: collection fails because `mini_gpt.vts_motion` does not exist.

- [ ] **Step 3: Implement the pure policy**

Create `vts_motion.py` with these public values and interfaces:

```python
IDLE_SWAY_AMPLITUDE = 5.0
IDLE_SWAY_SPEED = 1.5
SPEAKING_SWAY_AMPLITUDE = 8.0
SPEAKING_SWAY_SPEED = 1.35
MICRO_ACTION_MIN_DELAY = 12.0
MICRO_ACTION_MAX_DELAY = 25.0


def sway_value(elapsed: float, *, speaking: bool) -> float:
    amplitude = SPEAKING_SWAY_AMPLITUDE if speaking else IDLE_SWAY_AMPLITUDE
    speed = SPEAKING_SWAY_SPEED if speaking else IDLE_SWAY_SPEED
    return math.sin(elapsed * speed) * amplitude


def is_safe_micro_action(label: str, identifier: str) -> bool:
    searchable = f"{label} {identifier}".casefold()
    if any(term in searchable for term in ("^", "pout", "blush", "脸红", "衣", "服", "胸", "腿", "鞋")):
        return False
    return any(
        term in searchable
        for term in ("blink", "eye", "close", "restore", "眼", "闭", "恢复", "ear", "耳", "tail", "尾", "head", "头", "nod", "tilt")
    )
```

Implement `MicroActionScheduler` with an injected `random_source`, a
`next_due_at` property, `update(now, speaking) -> bool`, and `reset()`. On the
first speaking update, set `next_due_at` to `now + uniform(12, 25)`; return
true only when `now >= next_due_at`, then schedule the next interval. A
non-speaking update calls `reset()` and returns false.

- [ ] **Step 4: Run the test and verify it passes**

Run the same pytest command. Expected: all motion-policy tests pass.

- [ ] **Step 5: Commit the isolated policy**

```powershell
git add src/mini_gpt/vts_motion.py tests/test_vts_motion.py
git commit -m "feat: add balanced avatar motion policy"
```

### Task 2: Integrate reduced sway and speech-session timing

**Files:**
- Modify: `src/mini_gpt/vts_manager.py:1-150`
- Modify: `src/mini_gpt/gui.py:1100-1190`
- Test: `tests/test_vts_motion.py`

- [ ] **Step 1: Add integration tests for the manager's sway values**

Test the pure function at the manager boundary by monkeypatching
`vts_manager.sway_value` and asserting that `sway_loop` requests use the
speaking flag. Use a fake async request, a fake `requestSetParameterValue`, a
short `asyncio.sleep` that stops after two iterations, and a manager with
`is_connected=True`.

- [ ] **Step 2: Run the new integration test and verify it fails**

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_vts_motion.py -q
```

Expected: the manager still calls hard-coded `sin(...)*30` and the new
assertion fails.

- [ ] **Step 3: Connect the policy to `VTSManager`**

Import `MicroActionScheduler` and `sway_value`. Change the constructor to
accept `random_source=None`, set `self._random = random_source or random.Random()`,
and construct one scheduler with that source. Replace the speaking and
idle math in `sway_loop` with:

```python
elapsed = time.monotonic() - start_time
speaking = self.is_speaking
val = sway_value(elapsed, speaking=speaking)
```

Call the automatic-action hook only when `scheduler.update(monotonic_now,
speaking)` returns true; never perform catalog I/O on every 50 ms sway tick.
Add `set_speaking(self, speaking: bool)`, which updates `is_speaking` and calls
`scheduler.reset()` when speech stops or when a new speech session starts.

Update both Gwen playback boundaries in `RuApp._play_gwen_chunks` to call
`self.vts_manager.set_speaking(True/False)` rather than assigning
`is_speaking` directly. Keep the existing `VoiceVolume` path untouched.

- [ ] **Step 4: Run the focused manager and existing VTS tests**

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_vts_motion.py tests\test_vts_catalog.py tests\test_vts_actions.py -q
```

Expected: all pass; the speaking peak is at most `8.0` and idle behavior is
unchanged.

- [ ] **Step 5: Commit the sway integration**

```powershell
git add src/mini_gpt/vts_manager.py src/mini_gpt/gui.py tests/test_vts_motion.py
git commit -m "feat: soften Ru speaking sway"
```

### Task 3: Add safe dynamic micro-actions with model revalidation

**Files:**
- Modify: `src/mini_gpt/vts_manager.py:150-260`
- Modify: `src/mini_gpt/vts_motion.py`
- Test: `tests/test_vts_motion.py`

- [ ] **Step 1: Write failing candidate-selection tests**

Use catalog-like objects with `label` and `identifier` fields and assert that
the policy returns only safe entries, excludes `^`, blush, clothing and
unknown entries, and returns no duplicate candidate. Add a test that an empty
safe pool produces no request.

- [ ] **Step 2: Run the test to verify it fails**

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_vts_motion.py -q
```

Expected: the candidate-selection interface is missing.

- [ ] **Step 3: Implement model-aware selection and guarded dispatch**

Add `safe_micro_action_entries(entries)` to `vts_motion.py`, preserving catalog
entry objects whose label/identifier passes `is_safe_micro_action`. In
`VTSManager`, add `_maybe_trigger_micro_action()` that:

1. Fetches the current catalog only when the scheduler says one is due.
2. Filters `catalog.entries` to safe `kind == "hotkey"` entries.
3. Re-fetches/rechecks the current model ID immediately before dispatch.
4. Selects one entry with the manager's injected random source.
5. Calls the existing VTube trigger request by hotkey identifier.
6. Records the action time and skips if a semantic action is in flight.

Use a dedicated `threading.Lock`/async guard around the actual trigger request,
not around the 50 ms sway write. Log a skipped action at debug level only; a
missing catalog or changed model must never raise to the GUI.

- [ ] **Step 4: Run tests for safe selection and semantic-action coexistence**

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_vts_motion.py tests\test_vts_actions.py tests\test_vts_catalog.py -q
```

Expected: safe actions dispatch, unsafe actions do not, and existing semantic
facial/body action tests remain green.

- [ ] **Step 5: Commit the dynamic micro-action layer**

```powershell
git add src/mini_gpt/vts_motion.py src/mini_gpt/vts_manager.py tests/test_vts_motion.py
git commit -m "feat: add safe dynamic avatar micro-actions"
```

### Task 4: Documentation and full verification

**Files:**
- Modify: `README.md` in the VTube Studio motion section
- Modify: `docs/ANTIGRAVITY_HANDOFF.md` in the current voice/VTube status

- [ ] **Step 1: Update the user-facing documentation**

Document the new speaking sway amplitude, the 12-25 second automatic-action
window, the safe-action exclusions, and the fact that LLM-selected expressions
remain active. Do not claim that every model has the same hotkeys; state that
the current catalog is filtered dynamically.

- [ ] **Step 2: Run the full verification partitions**

```powershell
& .\.venv\Scripts\python.exe -m compileall -q src
& .\.venv\Scripts\python.exe -m pytest -q --ignore=tests\test_gwen_tts_server.py
& .\gwen_env\Scripts\python.exe -m pytest tests\test_gwen_tts_server.py -q
git diff --check
```

Expected: compilation succeeds, the main suite and Gwen partition pass, and
Git reports no whitespace errors.

- [ ] **Step 3: Review documents for stale claims**

Search `README.md` and `docs` for the old speaking amplitude `30`, claims that
sway is removed, or claims that automatic actions are disabled. Update only
current-status documents; leave historical plans as historical records.

- [ ] **Step 4: Commit documentation and final verification**

```powershell
git add README.md docs/ANTIGRAVITY_HANDOFF.md
git commit -m "docs: describe balanced Ru avatar motion"
git status --short
```

Expected: the working tree contains only unrelated user changes, if any;
runtime artifacts remain ignored and no credentials are staged.
