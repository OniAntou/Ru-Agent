# Ru VTube Organic Motion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Ru's repetitive speaking sway with bounded layered motion and smooth speech-state transitions.

**Architecture:** Keep `LLM_Sway` and `VoiceVolume` unchanged. A pure motion module calculates a layered target and an `OrganicSwayController` eases the live value using monotonic frame time; `VTSManager` owns the existing 50 ms request loop and micro-action scheduler.

**Tech Stack:** Python 3.13, `asyncio`, `math`, `pytest`, pyvts, Tkinter GUI.

---

### Task 1: Add failing tests for organic motion

**Files:**
- Modify: `tests/test_vts_motion.py`
- Test target: `src/mini_gpt/vts_motion.py`

- [ ] **Step 1: Write the failing tests**

Add:

```python
from mini_gpt.vts_motion import OrganicSwayController


def test_layered_sway_stays_bounded_and_is_not_a_single_repeating_wave():
    values = [sway_value(index * 0.1, speaking=True) for index in range(240)]
    assert max(abs(value) for value in values) <= SPEAKING_SWAY_AMPLITUDE
    assert len({round(value, 4) for value in values[:80]}) > 60
    assert values[:20] != values[120:140]


def test_controller_eases_into_and_out_of_speaking_motion():
    controller = OrganicSwayController()
    idle = controller.update(now=0.0, elapsed=0.0, speaking=False)
    target = sway_value(0.05, speaking=True)
    speaking_frame = controller.update(now=0.05, elapsed=0.05, speaking=True)
    stopped_frame = controller.update(now=0.10, elapsed=0.10, speaking=False)
    assert idle == 0.0
    assert abs(speaking_frame) < abs(target)
    assert abs(stopped_frame - speaking_frame) < abs(speaking_frame)
```

- [ ] **Step 2: Run the focused test and verify it fails**

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_vts_motion.py -q
```

Expected: collection or attribute failure because `OrganicSwayController` is
not implemented.

### Task 2: Implement the pure organic motion controller

**Files:**
- Modify: `src/mini_gpt/vts_motion.py`
- Modify: `tests/test_vts_motion.py`

- [ ] **Step 1: Replace the single sine target**

Implement this bounded layered target while preserving the existing constants:

```python
def sway_value(elapsed: float, speaking: bool) -> float:
    amplitude = SPEAKING_SWAY_AMPLITUDE if speaking else IDLE_SWAY_AMPLITUDE
    time_value = float(elapsed)
    layered = (
        0.64 * math.sin(time_value * 1.05)
        + 0.24 * math.sin(time_value * 0.47 + 1.7)
        + 0.12 * math.sin(time_value * 2.2 + 0.4)
    )
    envelope = 0.82 + 0.18 * (0.5 + 0.5 * math.sin(time_value * 0.17 + 0.8))
    return amplitude * layered * envelope
```

- [ ] **Step 2: Add easing state**

Implement `OrganicSwayController` with `_value` and `_last_now`. Its
`update(now, elapsed, speaking)` computes `target = sway_value(...)`, uses
`alpha = 1 - math.exp(-delta / 0.22)`, and moves `_value` toward `target`.
The first frame returns `0.0` and initializes state; `reset()` clears both
fields.

- [ ] **Step 3: Run the focused tests**

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_vts_motion.py -q
```

Expected: all motion-policy, scheduler, and filter tests pass.

- [ ] **Step 4: Commit the pure motion change**

```powershell
git add src/mini_gpt/vts_motion.py tests/test_vts_motion.py
git commit -m "feat: add organic avatar sway controller"
```

### Task 3: Connect the controller to VTube Studio

**Files:**
- Modify: `src/mini_gpt/vts_manager.py:24-170`
- Test: `tests/test_vts_motion.py`

- [ ] **Step 1: Add manager-boundary coverage**

Use a fake request client and assert that two sway-loop frames use controller
output while the 50 ms cadence and micro-action scheduler remain unchanged.

- [ ] **Step 2: Replace direct sway calculation**

Create `self._sway_controller = OrganicSwayController()` and use:

```python
now = time.monotonic()
elapsed = now - start_time
val = self._sway_controller.update(now, elapsed, self.is_speaking)
```

Reset the controller when the loop starts after reconnect and in `shutdown()`.

- [ ] **Step 3: Run integration checks**

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_vts_motion.py tests\test_overlay_control.py -q
& .\.venv\Scripts\python.exe -m py_compile src\mini_gpt\vts_motion.py src\mini_gpt\vts_manager.py src\mini_gpt\gui.py
```

Expected: focused tests pass and compilation succeeds.

- [ ] **Step 4: Commit the VTS integration**

```powershell
git add src/mini_gpt/vts_manager.py tests/test_vts_motion.py
git commit -m "fix: smooth Ru speaking sway transitions"
```

### Task 4: Update documentation and verify the full project

**Files:**
- Modify: `README.md` in the VTube Studio section
- Modify: `docs/ANTIGRAVITY_HANDOFF.md` in the current VTube motion status

- [ ] **Step 1: Update current motion claims**

Document layered/eased speaking motion, the `14.0` bound, and unchanged
`VoiceVolume` lip-sync. Leave historical design documents unchanged.

- [ ] **Step 2: Run full verification**

```powershell
& .\.venv\Scripts\python.exe -m compileall -q src
& .\.venv\Scripts\python.exe -m pytest -q --ignore=tests\test_gwen_tts_server.py
& .\gwen_env\Scripts\python.exe -m pytest tests\test_omnivoice_tts_server.py -q
git diff --check
```

Expected: compilation succeeds, both test partitions pass, and Git reports no
whitespace errors.

- [ ] **Step 3: Commit documentation**

```powershell
git add README.md docs/ANTIGRAVITY_HANDOFF.md
git commit -m "docs: describe organic Ru avatar motion"
```
