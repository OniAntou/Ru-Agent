# VTS Motion FSM & Control Panel — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace random-timer motion scheduling with a context-driven FSM (IDLE/LISTENING/THINKING/SPEAKING/POST_REPLY), add emotion-aware micro-actions/poses, tunable motion config via `/vts/config` + GUI sliders, and a reconnect policy that never permanently gives up.

**Architecture:** Pure-Python modules (`vts_emotion`, `vts_config`, `vts_context`, `vts_state`) feed an extended `VTSManager` whose existing asyncio sway loop now consumes a state machine; FastAPI router persists/validates `MotionConfig`; WinUI3 `VoiceView` exposes sliders + live state label and `ChatView` pushes typing activity.

**Tech Stack:** Python 3.11+ (FastAPI, pydantic, pyvts), WinUI 3 / .NET 10 (C#), pytest.

## Global Constraints

- Python ≥3.11, venv at `D:\LLM\.venv`; run pytest as `& "D:\LLM\.venv\Scripts\python.exe" -m pytest <args>`
- No new Python or NuGet dependencies
- All motion logic must run on the existing VTS sway loop (no new threads/timers; keep `time.monotonic()` for all timing)
- Emotion tags use format `[[EMO:<tag>]]`; VTS codes keep format `[[VTS:<codes>]]`
- Slider mapping (0–100%): Sway → amplitude lerp (idle 5→30, speaking 5→40); Đầu → `head_scale` %; Mắt → `eye_scale` %; Micro → delay lerp (100% = min delays); Pose → interval lerp (100% = min intervals)
- Follow existing import grouping (stdlib → third-party → internal), Conventional Commits with scope
- Existing tests must keep passing: `test_vts_catalog_actions.py`, `test_vts_motion.py`

---

### Task 1: Emotion extraction (`vts_emotion.py`)

**Files:**
- Create: `src/mini_gpt/vts_emotion.py`
- Test: `tests/test_vts_emotion.py`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `class EmotionTag(str, enum.Enum)` — members `NONE="none"`, `JOY="joy"`, `SAD="sad"`, `ANGRY="angry"`, `SURPRISED="surprised"`, `SHY="shy"`
  - `extract_reply_emotion(reply: str) -> tuple[str, EmotionTag]` — returns `(reply_without_emo_markers, tag)`; valid marker wins, malformed/absent marker → Vietnamese keyword heuristic → `NONE`

- [ ] **Step 1: Write the failing test**

Create `tests/test_vts_emotion.py`:

```python
from mini_gpt.vts_emotion import EmotionTag, extract_reply_emotion


def test_valid_tag_wins_and_is_stripped():
    cleaned, tag = extract_reply_emotion("Vui quá đi! [[EMO:joy]]")
    assert cleaned == "Vui quá đi!"
    assert tag == EmotionTag.JOY


def test_unknown_tag_falls_back_to_heuristic_and_is_stripped():
    cleaned, tag = extract_reply_emotion("Em buồn quá [[EMO:xyz]]")
    assert cleaned == "Em buồn quá"
    assert tag == EmotionTag.SAD


def test_no_tag_uses_vietnamese_heuristic():
    _, tag = extract_reply_emotion("Trời ơi, ngạc nhiên thật!")
    assert tag == EmotionTag.SURPRISED
    _, tag = extract_reply_emotion("Em xấu hổ quá đi mất")
    assert tag == EmotionTag.SHY
    _, tag = extract_reply_emotion("Câu trả lời trung tính bình thường")
    assert tag == EmotionTag.NONE


def test_heuristic_normalizes_diacritics():
    _, tag = extract_reply_emotion("Anh nói vậy em giận anh đấy!")
    assert tag == EmotionTag.ANGRY


def test_marker_mid_sentence_is_removed_but_text_kept():
    cleaned, tag = extract_reply_emotion("Hôm nay [[EMO:sad]] em hơi mệt")
    assert cleaned == "Hôm nay em hơi mệt"
    assert tag == EmotionTag.SAD
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_emotion.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'mini_gpt.vts_emotion'`

- [ ] **Step 3: Write minimal implementation**

Create `src/mini_gpt/vts_emotion.py`:

```python
from __future__ import annotations

import enum
import re
import unicodedata

EMOTION_MARKER = re.compile(r"\[\[EMO:([a-zA-Z_]+)\]\]", re.IGNORECASE)
_STRIP_EMOTION_MARKER = re.compile(r"\[\[EMO:[a-zA-Z_]+\]\]", re.IGNORECASE)


class EmotionTag(str, enum.Enum):
    NONE = "none"
    JOY = "joy"
    SAD = "sad"
    ANGRY = "angry"
    SURPRISED = "surprised"
    SHY = "shy"


_JOY_PATTERNS = (r"\b(vui|mung|cuoi|ha ha|he he|phan khoi|thich lam|tuyet voi)\b",)
_SAD_PATTERNS = (r"\b(buon|buon ba|tang than|met moi|chan nan)\b",)
_ANGRY_PATTERNS = (r"\b(gian|tuc|buc min|nong gian)\b",)
_SURPRISED_PATTERNS = (r"\b(ngac nhien|giat minh|oi gioi|trui ui)\b",)
_SHY_PATTERNS = (r"\b(xau ho|ngai ngu|then tho)\b",)


def _normalize(text: str) -> str:
    normalized = unicodedata.normalize("NFD", text.lower())
    normalized = "".join(char for char in normalized if unicodedata.category(char) != "Mn")
    return normalized.replace("đ", "d")


def _match_any(text: str, patterns: tuple[str, ...]) -> bool:
    return any(re.search(pattern, text) for pattern in patterns)


def _heuristic_emotion(reply: str) -> EmotionTag:
    text = _normalize(reply)
    if _match_any(text, _JOY_PATTERNS):
        return EmotionTag.JOY
    if _match_any(text, _SAD_PATTERNS):
        return EmotionTag.SAD
    if _match_any(text, _ANGRY_PATTERNS):
        return EmotionTag.ANGRY
    if _match_any(text, _SURPRISED_PATTERNS):
        return EmotionTag.SURPRISED
    if _match_any(text, _SHY_PATTERNS):
        return EmotionTag.SHY
    return EmotionTag.NONE


def extract_reply_emotion(reply: str) -> tuple[str, EmotionTag]:
    cleaned = _STRIP_EMOTION_MARKER.sub("", reply).strip()
    match = EMOTION_MARKER.search(reply)
    if match:
        try:
            return cleaned, EmotionTag(match.group(1).lower())
        except ValueError:
            return cleaned, _heuristic_emotion(reply)
    return cleaned, _heuristic_emotion(reply)
```

Note: the heuristic checks are ordered joy → sad → angry → surprised → shy; "Trời ơi, ngạc nhiên thật!" matches surprised; "Em xấu hổ" matches shy; a reply containing both joy and sad words returns JOY (first match wins).

- [ ] **Step 4: Run test to verify it passes**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_emotion.py -v`
Expected: 5 passed

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/vts_emotion.py tests/test_vts_emotion.py
git commit -m "feat(vts): add reply emotion extraction with LLM tag + Vietnamese heuristic"
```

---

### Task 2: Motion config model (`vts_config.py`)

**Files:**
- Create: `src/mini_gpt/vts_config.py`
- Test: `tests/test_vts_config.py`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `FLOAT_FIELDS: dict[str, tuple[float, float, float]]` — name → (min, max, default)
  - `@dataclass(frozen=True) class MotionConfig` with 10 float fields (defaults match FLOAT_FIELDS), plus:
    - `to_dict(self) -> dict[str, float]`
    - `from_dict(cls, data: dict) -> MotionConfig` — raises `ValueError` with a clear message on missing/non-numeric/out-of-range field or `max < min` ordering

- [ ] **Step 1: Write the failing test**

Create `tests/test_vts_config.py`:

```python
import pytest

from mini_gpt.vts_config import FLOAT_FIELDS, MotionConfig


def test_defaults_match_policy_ranges():
    config = MotionConfig.defaults()
    assert config.sway_amplitude_idle == 10.0
    assert config.sway_amplitude_speaking == 16.0
    assert config.micro_action_min_delay == 8.0
    assert config.micro_action_max_delay == 22.0
    assert config.pose_min_interval == 90.0
    assert config.pose_max_interval == 130.0
    assert config.breath_amplitude == 1.5
    assert config.breath_rate == 0.15
    for name in FLOAT_FIELDS:
        assert hasattr(config, name)


def test_round_trip_to_and_from_dict():
    config = MotionConfig(sway_amplitude_idle=18.0, head_scale=150.0)
    restored = MotionConfig.from_dict(config.to_dict())
    assert restored == config


def test_from_dict_rejects_out_of_range():
    with pytest.raises(ValueError, match="sway_amplitude_idle"):
        MotionConfig.from_dict({**MotionConfig.defaults().to_dict(), "sway_amplitude_idle": 999.0})


def test_from_dict_rejects_non_numeric():
    with pytest.raises(ValueError, match="breath_rate"):
        MotionConfig.from_dict({**MotionConfig.defaults().to_dict(), "breath_rate": "nhanh"})


def test_from_dict_rejects_missing_field():
    data = MotionConfig.defaults().to_dict()
    del data["eye_scale"]
    with pytest.raises(ValueError, match="eye_scale"):
        MotionConfig.from_dict(data)


def test_from_dict_enforces_min_max_ordering():
    data = MotionConfig.defaults().to_dict()
    data["micro_action_min_delay"] = 30.0
    data["micro_action_max_delay"] = 10.0
    with pytest.raises(ValueError, match="micro_action_max_delay"):
        MotionConfig.from_dict(data)
    data = MotionConfig.defaults().to_dict()
    data["pose_min_interval"] = 500.0
    data["pose_max_interval"] = 50.0
    with pytest.raises(ValueError, match="pose_max_interval"):
        MotionConfig.from_dict(data)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_config.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'mini_gpt.vts_config'`

- [ ] **Step 3: Write minimal implementation**

Create `src/mini_gpt/vts_config.py`:

```python
from __future__ import annotations

from dataclasses import dataclass

# name -> (minimum, maximum, default)
FLOAT_FIELDS: dict[str, tuple[float, float, float]] = {
    "sway_amplitude_idle": (5.0, 30.0, 10.0),
    "sway_amplitude_speaking": (5.0, 40.0, 16.0),
    "head_scale": (0.0, 200.0, 100.0),
    "eye_scale": (0.0, 200.0, 100.0),
    "micro_action_min_delay": (1.0, 120.0, 8.0),
    "micro_action_max_delay": (1.0, 120.0, 22.0),
    "pose_min_interval": (10.0, 600.0, 90.0),
    "pose_max_interval": (10.0, 600.0, 130.0),
    "breath_amplitude": (0.0, 5.0, 1.5),
    "breath_rate": (0.05, 0.5, 0.15),
}


@dataclass(frozen=True)
class MotionConfig:
    sway_amplitude_idle: float = 10.0
    sway_amplitude_speaking: float = 16.0
    head_scale: float = 100.0
    eye_scale: float = 100.0
    micro_action_min_delay: float = 8.0
    micro_action_max_delay: float = 22.0
    pose_min_interval: float = 90.0
    pose_max_interval: float = 130.0
    breath_amplitude: float = 1.5
    breath_rate: float = 0.15

    @classmethod
    def defaults(cls) -> "MotionConfig":
        return cls()

    def to_dict(self) -> dict[str, float]:
        return {name: getattr(self, name) for name in FLOAT_FIELDS}

    @classmethod
    def from_dict(cls, data: dict) -> "MotionConfig":
        values: dict[str, float] = {}
        for name, (minimum, maximum, _default) in FLOAT_FIELDS.items():
            if name not in data:
                raise ValueError(f"missing motion config field: {name}")
            raw = data[name]
            try:
                value = float(raw)
            except (TypeError, ValueError):
                raise ValueError(f"{name} must be a number, got {raw!r}") from None
            if not minimum <= value <= maximum:
                raise ValueError(f"{name} must be between {minimum} and {maximum}, got {value}")
            values[name] = value
        if values["micro_action_max_delay"] < values["micro_action_min_delay"]:
            raise ValueError(
                f"micro_action_max_delay must be >= micro_action_min_delay "
                f"({values['micro_action_max_delay']} < {values['micro_action_min_delay']})"
            )
        if values["pose_max_interval"] < values["pose_min_interval"]:
            raise ValueError(
                f"pose_max_interval must be >= pose_min_interval "
                f"({values['pose_max_interval']} < {values['pose_min_interval']})"
            )
        return cls(**values)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_config.py -v`
Expected: 6 passed

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/vts_config.py tests/test_vts_config.py
git commit -m "feat(vts): add validated MotionConfig model"
```

---

### Task 3: Context provider (`vts_context.py`)

**Files:**
- Create: `src/mini_gpt/vts_context.py`
- Test: `tests/test_vts_context.py`

**Interfaces:**
- Consumes: `EmotionTag` from `mini_gpt.vts_emotion`
- Produces:
  - `@dataclass(frozen=True) class MotionContextSnapshot` — fields `speaking: bool = False`, `generating: bool = False`, `user_typing: bool = False`, `system_idle: bool = False`, `emotion: EmotionTag = EmotionTag.NONE`
  - `class MotionContextProvider` — `set_speaking(bool)`, `set_generating(bool)`, `set_user_typing(bool)`, `set_emotion(EmotionTag | str)`, `set_system_idle(bool)`, `snapshot() -> MotionContextSnapshot`; thread-safe; `set_emotion` coerces unknown strings to `EmotionTag.NONE`

- [ ] **Step 1: Write the failing test**

Create `tests/test_vts_context.py`:

```python
import threading

from mini_gpt.vts_context import MotionContextProvider
from mini_gpt.vts_emotion import EmotionTag


def test_snapshot_defaults_are_safe():
    snap = MotionContextProvider().snapshot()
    assert snap.speaking is False
    assert snap.generating is False
    assert snap.user_typing is False
    assert snap.system_idle is False
    assert snap.emotion == EmotionTag.NONE


def test_setters_update_snapshot():
    provider = MotionContextProvider()
    provider.set_speaking(True)
    provider.set_generating(True)
    provider.set_user_typing(True)
    provider.set_emotion(EmotionTag.JOY)
    snap = provider.snapshot()
    assert snap.speaking and snap.generating and snap.user_typing
    assert snap.emotion == EmotionTag.JOY


def test_emotion_accepts_string_and_coerces_unknown():
    provider = MotionContextProvider()
    provider.set_emotion("sad")
    assert provider.snapshot().emotion == EmotionTag.SAD
    provider.set_emotion("madeup")
    assert provider.snapshot().emotion == EmotionTag.NONE


def test_snapshots_are_immutable_after_set():
    provider = MotionContextProvider()
    provider.set_speaking(True)
    first = provider.snapshot()
    provider.set_speaking(False)
    assert first.speaking is True
    assert provider.snapshot().speaking is False


def test_concurrent_updates_do_not_raise():
    provider = MotionContextProvider()
    errors: list[BaseException] = []

    def worker(value: int) -> None:
        try:
            for _ in range(200):
                provider.set_speaking(value % 2 == 0)
                provider.set_generating(value % 3 == 0)
                provider.set_emotion(EmotionTag.JOY if value % 2 else EmotionTag.SAD)
                provider.snapshot()
        except BaseException as exc:  # pragma: no cover
            errors.append(exc)

    threads = [threading.Thread(target=worker, args=(index,)) for index in range(6)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert errors == []
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_context.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'mini_gpt.vts_context'`

- [ ] **Step 3: Write minimal implementation**

Create `src/mini_gpt/vts_context.py`:

```python
from __future__ import annotations

import dataclasses
import threading
from dataclasses import dataclass

from .vts_emotion import EmotionTag


@dataclass(frozen=True)
class MotionContextSnapshot:
    speaking: bool = False
    generating: bool = False
    user_typing: bool = False
    system_idle: bool = False
    emotion: EmotionTag = EmotionTag.NONE


class MotionContextProvider:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._snap = MotionContextSnapshot()

    def set_speaking(self, speaking: bool) -> None:
        self._set(speaking=bool(speaking))

    def set_generating(self, generating: bool) -> None:
        self._set(generating=bool(generating))

    def set_user_typing(self, typing: bool) -> None:
        self._set(user_typing=bool(typing))

    def set_system_idle(self, idle: bool) -> None:
        self._set(system_idle=bool(idle))

    def set_emotion(self, emotion: EmotionTag | str) -> None:
        if isinstance(emotion, str):
            try:
                emotion = EmotionTag(emotion)
            except ValueError:
                emotion = EmotionTag.NONE
        self._set(emotion=emotion)

    def snapshot(self) -> MotionContextSnapshot:
        with self._lock:
            return self._snap

    def _set(self, **kwargs: object) -> None:
        with self._lock:
            self._snap = dataclasses.replace(self._snap, **kwargs)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_context.py -v`
Expected: 5 passed

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/vts_context.py tests/test_vts_context.py
git commit -m "feat(vts): add thread-safe motion context provider"
```

---

### Task 4: State machine (`vts_state.py`)

**Files:**
- Create: `src/mini_gpt/vts_state.py`
- Test: `tests/test_vts_state.py`

**Interfaces:**
- Consumes: `MotionContextSnapshot` from `mini_gpt.vts_context`
- Produces:
  - `class VtsMotionState(str, enum.Enum)` — `IDLE="idle"`, `LISTENING="listening"`, `THINKING="thinking"`, `SPEAKING="speaking"`, `POST_REPLY="post_reply"`
  - Constants `POST_REPLY_DWELL_SECONDS = 4.0`, `LISTENING_ENTER_IDLE_SECONDS = 6.0`, `MIN_STATE_DWELL_SECONDS = 0.5`
  - `class MotionStateMachine` — `state -> VtsMotionState`, `state_elapsed(now) -> float`, `reset(now)`, `update(snap, now) -> VtsMotionState`
  - Transition priority: `speaking` → SPEAKING, else `generating` → THINKING, else `user_typing` → LISTENING, else rest-target rules; SPEAKING/THINKING may interrupt instantly, other transitions need `MIN_STATE_DWELL_SECONDS`

- [ ] **Step 1: Write the failing test**

Create `tests/test_vts_state.py`:

```python
from mini_gpt.vts_context import MotionContextProvider
from mini_gpt.vts_state import (
    LISTENING_ENTER_IDLE_SECONDS,
    MIN_STATE_DWELL_SECONDS,
    POST_REPLY_DWELL_SECONDS,
    MotionStateMachine,
    VtsMotionState,
)


def _snap(**kwargs):
    return MotionContextProvider().set_speaking(kwargs.get("speaking", False)) or MotionContextProvider().snapshot()


def test_starts_idle_and_stays_idle():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    assert machine.state == VtsMotionState.IDLE
    assert machine.update(_snap(), now=1.0) == VtsMotionState.IDLE


def test_speaking_is_authoritative():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(speaking=True, generating=True, user_typing=True), now=0.1)
    assert machine.state == VtsMotionState.SPEAKING


def test_generating_moves_to_thinking_instantly():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(generating=True), now=0.1)
    assert machine.state == VtsMotionState.THINKING


def test_user_typing_moves_to_listening_with_dwell():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(user_typing=True), now=0.1)
    assert machine.state == VtsMotionState.LISTENING
    machine.update(_snap(), now=0.1 + MIN_STATE_DWELL_SECONDS + 0.01)
    assert machine.state == VtsMotionState.LISTENING  # still inside 6s window


def test_listening_returns_to_idle_after_timeout():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(user_typing=True), now=0.1)
    machine.update(_snap(user_typing=True), now=1.0)
    machine.update(_snap(), now=1.0 + LISTENING_ENTER_IDLE_SECONDS + 0.01)
    assert machine.state == VtsMotionState.IDLE


def test_speech_stop_enters_post_reply_then_idle_after_dwell():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(speaking=True), now=0.1)
    assert machine.state == VtsMotionState.SPEAKING
    machine.update(_snap(), now=0.2)
    assert machine.state == VtsMotionState.POST_REPLY
    machine.update(_snap(), now=0.2 + POST_REPLY_DWELL_SECONDS + 0.01)
    assert machine.state == VtsMotionState.IDLE


def test_generating_stop_returns_to_idle():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(generating=True), now=0.1)
    machine.update(_snap(), now=0.2)
    assert machine.state == VtsMotionState.IDLE


def test_quick_typing_flap_does_not_leave_idle_before_dwell():
    machine = MotionStateMachine()
    machine.reset(now=0.0)
    machine.update(_snap(user_typing=True), now=0.1)
    assert machine.state == VtsMotionState.LISTENING
    machine.update(_snap(), now=0.2)
    assert machine.state == VtsMotionState.LISTENING  # dwell gate
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_state.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'mini_gpt.vts_state'`

- [ ] **Step 3: Write minimal implementation**

Create `src/mini_gpt/vts_state.py`:

```python
from __future__ import annotations

import enum

from .vts_context import MotionContextSnapshot


class VtsMotionState(str, enum.Enum):
    IDLE = "idle"
    LISTENING = "listening"
    THINKING = "thinking"
    SPEAKING = "speaking"
    POST_REPLY = "post_reply"


POST_REPLY_DWELL_SECONDS = 4.0
LISTENING_ENTER_IDLE_SECONDS = 6.0
MIN_STATE_DWELL_SECONDS = 0.5


class MotionStateMachine:
    def __init__(self) -> None:
        self._state = VtsMotionState.IDLE
        self._entry_at = 0.0

    @property
    def state(self) -> VtsMotionState:
        return self._state

    def state_elapsed(self, now: float) -> float:
        return max(0.0, now - self._entry_at)

    def reset(self, now: float) -> None:
        self._state = VtsMotionState.IDLE
        self._entry_at = now

    def update(self, snap: MotionContextSnapshot, now: float) -> VtsMotionState:
        if snap.speaking:
            desired = VtsMotionState.SPEAKING
        elif snap.generating:
            desired = VtsMotionState.THINKING
        elif snap.user_typing:
            desired = VtsMotionState.LISTENING
        else:
            desired = self._rest_target(now)

        if desired == self._state:
            return self._state
        if desired in (VtsMotionState.SPEAKING, VtsMotionState.THINKING):
            self._enter(desired, now)
        elif self.state_elapsed(now) >= MIN_STATE_DWELL_SECONDS:
            self._enter(desired, now)
        return self._state

    def _rest_target(self, now: float) -> VtsMotionState:
        if self._state == VtsMotionState.POST_REPLY:
            if self.state_elapsed(now) >= POST_REPLY_DWELL_SECONDS:
                return VtsMotionState.IDLE
            return VtsMotionState.POST_REPLY
        if self._state == VtsMotionState.SPEAKING:
            return VtsMotionState.POST_REPLY
        if self._state == VtsMotionState.THINKING:
            return VtsMotionState.IDLE
        if self._state == VtsMotionState.LISTENING:
            if self.state_elapsed(now) >= LISTENING_ENTER_IDLE_SECONDS:
                return VtsMotionState.IDLE
            return VtsMotionState.LISTENING
        return VtsMotionState.IDLE

    def _enter(self, state: VtsMotionState, now: float) -> None:
        self._state = state
        self._entry_at = now
```

- [ ] **Step 4: Run test to verify it passes**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_state.py -v`
Expected: 8 passed

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/vts_state.py tests/test_vts_state.py
git commit -m "feat(vts): add motion state machine with hysteresis"
```

---

### Task 5: Motion policies for FSM + config (`vts_motion.py`)

**Files:**
- Modify: `src/mini_gpt/vts_motion.py`
- Modify: `tests/test_vts_motion.py`
- Test: `tests/test_vts_motion.py`

**Interfaces:**
- Consumes: `MotionConfig` from `mini_gpt.vts_config`, `VtsMotionState` from `mini_gpt.vts_state`
- Produces (new/extended, existing names preserved with backward-compatible defaults):
  - `sway_value(elapsed, speaking, *, idle_amplitude=IDLE_SWAY_AMPLITUDE, speaking_amplitude=SPEAKING_SWAY_AMPLITUDE)` — added keyword args
  - `state_amplitude_scale(state) -> float` — IDLE 0.6, LISTENING 0.5, THINKING 0.3, SPEAKING 1.0, POST_REPLY 0.7
  - `class BreathController` — `value(now, config) -> float`
  - `OrganicSwayController.update(now, elapsed, speaking, *, config=None, scale=1.0)` — keyword args; when `config` given, uses `min(config.sway_amplitude_<idle|speaking> * scale, max)` amplitudes
  - `MicroActionScheduler.update(now, state, config) -> bool` — returns False (and resets timer) in LISTENING/THINKING; delays from config (idle = ×1.5)
  - `PoseCycleScheduler.update(now, state, config) -> bool` — POST_REPLY entry → one-shot True; SPEAKING/THINKING/LISTENING → False and timer reset; IDLE → interval from config

- [ ] **Step 1: Modify the existing failing test**

In `tests/test_vts_motion.py` replace `test_scheduler_waits_for_a_randomized_interval_and_resets_on_new_session` with:

```python
def test_scheduler_waits_for_a_randomized_interval_and_resets_on_new_session():
    from mini_gpt.vts_config import MotionConfig
    from mini_gpt.vts_state import VtsMotionState

    scheduler = MicroActionScheduler(random_source=FakeRandom([20.0, 14.0, 20.0]))
    config = MotionConfig.defaults()

    assert scheduler.update(100.0, VtsMotionState.SPEAKING, config) is False
    assert scheduler.update(119.9, VtsMotionState.SPEAKING, config) is False
    assert scheduler.update(120.0, VtsMotionState.SPEAKING, config) is True
    scheduler.update(121.0, VtsMotionState.IDLE, config)
    assert scheduler.update(122.0, VtsMotionState.SPEAKING, config) is False
    assert scheduler.next_due_at == 134.0
    scheduler.reset()
    assert scheduler.next_due_at is None
```

Append these new tests to `tests/test_vts_motion.py`:

```python
def test_scheduler_suppresses_micro_actions_while_listening_or_thinking():
    from mini_gpt.vts_config import MotionConfig
    from mini_gpt.vts_state import VtsMotionState

    scheduler = MicroActionScheduler(random_source=FakeRandom([1.0, 1.0]))
    config = MotionConfig.defaults()
    scheduler.next_due_at = 0.0  # force due

    assert scheduler.update(1.0, VtsMotionState.LISTENING, config) is False
    assert scheduler.next_due_at is None
    assert scheduler.update(1.0, VtsMotionState.THINKING, config) is False
    assert scheduler.next_due_at is None
    assert scheduler.update(1.0, VtsMotionState.SPEAKING, config) is False
    assert scheduler.next_due_at == 2.0


def test_breath_stays_bounded_and_uses_config():
    from mini_gpt.vts_config import MotionConfig
    from mini_gpt.vts_motion import BreathController

    controller = BreathController()
    config = MotionConfig.defaults()
    values = [controller.value(index * 0.1, config) for index in range(300)]
    assert max(abs(value) for value in values) <= config.breath_amplitude + 1e-9


def test_state_amplitude_scale_mapping():
    from mini_gpt.vts_motion import state_amplitude_scale
    from mini_gpt.vts_state import VtsMotionState

    assert state_amplitude_scale(VtsMotionState.SPEAKING) == 1.0
    assert state_amplitude_scale(VtsMotionState.IDLE) == 0.6
    assert state_amplitude_scale(VtsMotionState.THINKING) == 0.3
    assert state_amplitude_scale(VtsMotionState.POST_REPLY) == 0.7


def test_pose_scheduler_fires_once_on_post_reply_entry_and_idle_intervals():
    from mini_gpt.vts_config import MotionConfig
    from mini_gpt.vts_motion import PoseCycleScheduler
    from mini_gpt.vts_state import VtsMotionState

    scheduler = PoseCycleScheduler(random_source=FakeRandom([100.0, 100.0, 100.0]))
    config = MotionConfig.defaults()

    assert scheduler.update(10.0, VtsMotionState.POST_REPLY, config) is True
    assert scheduler.update(10.1, VtsMotionState.POST_REPLY, config) is False
    assert scheduler.update(10.2, VtsMotionState.SPEAKING, config) is False
    assert scheduler.update(15.0, VtsMotionState.IDLE, config) is False  # timer set at 10.2
    assert scheduler.update(110.2, VtsMotionState.IDLE, config) is True
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_motion.py -v`
Expected: FAIL — old scheduler signature mismatch (`update() got an unexpected keyword argument`) and `BreathController`/`state_amplitude_scale`/`PoseCycleScheduler.update` new signature missing

- [ ] **Step 3: Implement the changes**

Edit `src/mini_gpt/vts_motion.py`:

1. Add imports at top (after existing `import random` / `from typing import Iterable`):

```python
from .vts_config import MotionConfig
from .vts_state import VtsMotionState
```

2. Add state scaling + breath after the `_HEAD_YAW_AMPLITUDE` block:

```python
STATE_AMPLITUDE_SCALE = {
    VtsMotionState.IDLE: 0.6,
    VtsMotionState.LISTENING: 0.5,
    VtsMotionState.THINKING: 0.3,
    VtsMotionState.SPEAKING: 1.0,
    VtsMotionState.POST_REPLY: 0.7,
}


def state_amplitude_scale(state: VtsMotionState) -> float:
    return STATE_AMPLITUDE_SCALE.get(state, 1.0)


class BreathController:
    """Slow, bounded breathing layer added on top of body sway."""

    def __init__(self, random_source: object | None = None) -> None:
        self.random_source = random_source or random
        self._phase = self.random_source.uniform(0.0, 2.0 * math.pi)

    def value(self, now: float, config: MotionConfig) -> float:
        return config.breath_amplitude * math.sin(
            2.0 * math.pi * config.breath_rate * float(now) + self._phase
        )
```

3. Change `sway_value` signature to accept amplitudes:

```python
def sway_value(
    elapsed: float,
    speaking: bool,
    *,
    idle_amplitude: float = IDLE_SWAY_AMPLITUDE,
    speaking_amplitude: float = SPEAKING_SWAY_AMPLITUDE,
) -> float:
    amplitude = speaking_amplitude if speaking else idle_amplitude
    t = float(elapsed)
    layers = (
        0.50 * math.sin(t * 1.12 + 0.0)
        + 0.22 * math.sin(t * 0.47 + 1.7)
        + 0.16 * math.sin(t * 2.18 + 0.4)
        + 0.12 * math.sin(t * 0.73 + 2.9)
    )
    envelope = 0.60 + 0.40 * (0.5 + 0.5 * math.sin(t * 0.13 + 0.3))
    return amplitude * layers * envelope
```

4. Change `OrganicSwayController.update` to accept optional config/scale:

```python
    def update(
        self,
        now: float,
        elapsed: float,
        speaking: bool,
        *,
        config: MotionConfig | None = None,
        scale: float = 1.0,
    ) -> float:
        if self._last_now is None:
            self._last_now = now
            return self._value

        delta = max(0.0, float(now) - self._last_now)
        if config is None:
            amplitude = SPEAKING_SWAY_AMPLITUDE if speaking else IDLE_SWAY_AMPLITUDE
            idle_amplitude, speaking_amplitude = IDLE_SWAY_AMPLITUDE, SPEAKING_SWAY_AMPLITUDE
        else:
            idle_amplitude = min(config.sway_amplitude_idle * scale, 30.0)
            speaking_amplitude = min(config.sway_amplitude_speaking * scale, 40.0)
            amplitude = speaking_amplitude if speaking else idle_amplitude
        if speaking:
            if self._next_drift_at is None:
                self._next_drift_at = now + 0.8
            elif now >= self._next_drift_at:
                segment = math.floor(elapsed / 1.1)
                drift_wave = (
                    0.7 * math.sin(segment * 2.17 + 0.4)
                    + 0.3 * math.cos(segment * 1.31 + 1.8)
                )
                self._drift_target = amplitude * 0.11 * drift_wave
                hold = 0.55 + 0.75 * (0.5 + 0.5 * math.sin(segment * 1.71 + 0.2))
                self._next_drift_at = now + hold
        else:
            self._drift_target = 0.0
            self._next_drift_at = None

        drift_alpha = 1.0 - math.exp(-delta / 0.45)
        self._drift += (self._drift_target - self._drift) * drift_alpha
        target = sway_value(
            elapsed,
            speaking,
            idle_amplitude=idle_amplitude,
            speaking_amplitude=speaking_amplitude,
        ) + self._drift
        target = max(-amplitude, min(amplitude, target))
        alpha = 1.0 - math.exp(-delta / 0.22)
        self._value += (target - self._value) * alpha
        self._last_now = now
        return self._value
```

5. Replace `MicroActionScheduler.update` / `_next_delay` with:

```python
    def update(self, now: float, state: VtsMotionState, config: MotionConfig) -> bool:
        """Return ``True`` when a micro-action is due in the given state.

        Suppressed entirely in LISTENING/THINKING; idle timing is 1.5x sparser.
        """
        if state in (VtsMotionState.LISTENING, VtsMotionState.THINKING):
            self.next_due_at = None
            return False
        speaking = state == VtsMotionState.SPEAKING
        if self.next_due_at is None:
            self.next_due_at = now + self._next_delay(speaking, config)
            return False

        if now < self.next_due_at:
            return False

        self.next_due_at = now + self._next_delay(speaking, config)
        return True

    def _next_delay(self, speaking: bool, config: MotionConfig) -> float:
        minimum = config.micro_action_min_delay
        maximum = config.micro_action_max_delay
        if not speaking:
            minimum *= 1.5
            maximum *= 1.5
        return float(self.random_source.uniform(minimum, maximum))
```

6. Replace `PoseCycleScheduler.update` with:

```python
    def update(self, now: float, state: VtsMotionState, config: MotionConfig) -> bool:
        if state == VtsMotionState.POST_REPLY:
            if not self._post_reply_armed:
                self._post_reply_armed = True
                self._next_at = now + self._roll_interval(config)
                return True
            return False
        if state == VtsMotionState.SPEAKING:
            return False
        self._post_reply_armed = False
        if state != VtsMotionState.IDLE:
            self._next_at = None
            return False
        if self._next_at is None:
            self._next_at = now + self._roll_interval(config)
            return False
        if now < self._next_at:
            return False
        self._next_at = now + self._roll_interval(config)
        return True

    def _roll_interval(self, config: MotionConfig) -> float:
        return float(
            self.random_source.uniform(config.pose_min_interval, config.pose_max_interval)
        )
```

7. Add `self._post_reply_armed = False` to `PoseCycleScheduler.__init__` and `reset`:

```python
    def __init__(self, random_source: object | None = None) -> None:
        self.random_source = random_source or random
        self._next_at: float | None = None
        self._last_pose_code: str | None = None
        self._was_speaking = False
        self._post_reply_armed = False

    def reset(self) -> None:
        self._next_at = None
        self._last_pose_code = None
        self._was_speaking = False
        self._post_reply_armed = False
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_motion.py -v`
Expected: all pass (existing + 4 new)

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/vts_motion.py tests/test_vts_motion.py
git commit -m "feat(vts): state-aware motion policies, breath layer, configurable amplitudes"
```

---

### Task 6: Manager integration (`vts_manager.py`)

**Files:**
- Modify: `src/mini_gpt/vts_manager.py`
- Test: `tests/test_vts_manager.py`

**Interfaces:**
- Consumes: `MotionConfig` (`mini_gpt.vts_config`), `MotionContextProvider` (`mini_gpt.vts_context`), `MotionStateMachine`, `VtsMotionState` (`mini_gpt.vts_state`), `state_amplitude_scale`, `BreathController` (`mini_gpt.vts_motion`)
- Produces:
  - module-level `def reconnect_delay(attempts: int) -> float` — `min(5.0 + 5.0 * min(attempts, 5), 30.0)`
  - `VTSManager.apply_config(config: MotionConfig) -> None`
  - `VTSManager.note_speaking(bool)`, `note_generating(bool)`, `note_user_typing(bool)`, `note_emotion(EmotionTag | str)`
  - `VTSManager.current_state() -> VtsMotionState`
  - `VTSManager.config` attribute; `_context` provider; `_fsm` machine
  - `RECONNECT_MAX_ATTEMPTS` constant removed; `_schedule_reconnect`/`_auto_reconnect` use `reconnect_delay` without giving up

- [ ] **Step 1: Write the failing test**

Create `tests/test_vts_manager.py`:

```python
from mini_gpt.vts_config import MotionConfig
from mini_gpt.vts_manager import VTSManager, reconnect_delay


def test_reconnect_delay_grows_and_caps_at_30_seconds():
    assert reconnect_delay(0) == 5.0
    assert reconnect_delay(1) == 10.0
    assert reconnect_delay(5) == 30.0
    assert reconnect_delay(50) == 30.0


def test_apply_config_stores_config():
    manager = VTSManager()
    config = MotionConfig(sway_amplitude_idle=18.0)
    manager.apply_config(config)
    assert manager.config == config


def test_note_wrappers_update_context_snapshot():
    from mini_gpt.vts_emotion import EmotionTag

    manager = VTSManager()
    manager.note_speaking(True)
    manager.note_generating(True)
    manager.note_emotion(EmotionTag.JOY)
    snap = manager._context.snapshot()
    assert snap.speaking is True
    assert snap.generating is True
    assert snap.emotion == EmotionTag.JOY


def test_current_state_starts_idle_and_reacts_to_context():
    manager = VTSManager()
    assert manager.current_state().value == "idle"
    manager.note_generating(True)
    snap = manager._context.snapshot()
    manager._fsm.update(snap, now=0.5)
    assert manager.current_state().value == "thinking"


def test_apply_config_is_thread_safe_under_load():
    import threading

    manager = VTSManager()
    errors: list[BaseException] = []

    def worker() -> None:
        try:
            for index in range(200):
                manager.apply_config(MotionConfig(sway_amplitude_idle=5.0 + index % 20))
        except BaseException as exc:  # pragma: no cover
            errors.append(exc)

    threads = [threading.Thread(target=worker) for _ in range(4)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert errors == []
```

Note: `VTSManager()` does not connect; it only builds pyvts objects, so it is safe to construct in tests.

- [ ] **Step 2: Run test to verify it fails**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_manager.py -v`
Expected: FAIL — `reconnect_delay` undefined; no `apply_config`/`note_*`/`current_state`

- [ ] **Step 3: Implement the changes**

Edit `src/mini_gpt/vts_manager.py`:

1. Add imports after the existing `from .vts_motion import (...)` block:

```python
from .vts_config import MotionConfig
from .vts_context import MotionContextProvider
from .vts_emotion import EmotionTag
from .vts_motion import (
    BreathController,
    state_amplitude_scale,
)
from .vts_state import MotionStateMachine, VtsMotionState
```

2. Replace the reconnect constants block:

```python
REQUEST_TIMEOUT_SECONDS = 2.0
VTS_LOOP_WAIT_TIMEOUT_SECONDS = 10.0
SWAY_LOOP_SLEEP_SECONDS = 0.1
DISCONNECTED_POLL_SECONDS = 0.5
ERROR_BACKOFF_SECONDS = 1.0
RECONNECT_MIN_INTERVAL_SECONDS = 5.0
RECONNECT_BACKOFF_CAP_SECONDS = 30.0
MAX_CONCURRENT_REQUESTS = 4
CATALOG_CACHE_TTL_SECONDS = 10.0


def reconnect_delay(attempts: int) -> float:
    """Backoff that grows with attempts and caps, never gives up."""
    return min(RECONNECT_MIN_INTERVAL_SECONDS + 5.0 * min(attempts, 5), RECONNECT_BACKOFF_CAP_SECONDS)
```

3. In `__init__`, replace `self._reconnect_attempts = 0` … `self._reconnect_exhausted_logged = False` block with:

```python
        self._reconnect_attempts = 0
        self._context = MotionContextProvider()
        self._fsm = MotionStateMachine()
        self.config = MotionConfig.defaults()
        self._breath_controller = BreathController(self._motion_random)
        self._last_send_at = 0.0
```

(keep `_catalog_cache_at`, `_last_reconnect_attempt_at` as they are)

4. Add public methods after `set_injection_paused`:

```python
    def apply_config(self, config: MotionConfig) -> None:
        """Apply a validated motion config; survives disconnect/reconnect."""
        self.config = config
        logger.info("Applied VTS motion config: %s", config.to_dict())

    def note_speaking(self, speaking: bool) -> None:
        self._context.set_speaking(speaking)

    def note_generating(self, generating: bool) -> None:
        self._context.set_generating(generating)

    def note_user_typing(self, typing: bool) -> None:
        self._context.set_user_typing(typing)

    def note_emotion(self, emotion: EmotionTag | str) -> None:
        self._context.set_emotion(emotion)

    def current_state(self) -> VtsMotionState:
        return self._fsm.state
```

5. Rewrite `_schedule_reconnect` / `_auto_reconnect` to use `reconnect_delay` and never give up:

```python
    def _schedule_reconnect(self) -> None:
        if not self._sway_running:
            return
        now = time.monotonic()
        if now - self._last_reconnect_attempt_at < RECONNECT_MIN_INTERVAL_SECONDS:
            return
        self._last_reconnect_attempt_at = now
        logger.info("VTS connection lost, scheduling reconnect")
        self._spawn_background_task(self._auto_reconnect())

    async def _auto_reconnect(self) -> None:
        if self._reconnect_lock is None:
            self._reconnect_lock = asyncio.Lock()
        async with self._reconnect_lock:
            while self._sway_running and not self.is_connected:
                await asyncio.sleep(reconnect_delay(self._reconnect_attempts))
                if self.is_connected or not self._sway_running:
                    return
                self._reconnect_attempts += 1
                self._last_reconnect_attempt_at = time.monotonic()
                logger.info(
                    "VTS reconnect attempt %d",
                    self._reconnect_attempts,
                )
                await self._reconnect()
                if self.is_connected:
                    self._reconnect_attempts = 0
                    return
```

6. In `connect()`, after `self._catalog_cache = None`, replace the reset lines with:

```python
            self._reconnect_attempts = 0
            self._fsm.reset(time.monotonic())
            self._ensure_sway_task()
```

(also remove the `self._reconnect_exhausted_logged = False` line there)

7. In `_reconnect()`, replace the reset lines with:

```python
            self._reconnect_attempts = 0
            self._fsm.reset(time.monotonic())
            self._ensure_sway_task()
            logger.info("VTS reconnected")
```

8. Rewrite `sway_loop` body to drive the FSM, config and breath (replace the whole method):

```python
    async def sway_loop(self):
        self._sway_running = True
        self._sway_controller.reset()
        now0 = time.monotonic()
        self._fsm.reset(now0)
        start_time = now0
        prev_state: VtsMotionState | None = None
        try:
            while self._sway_running:
                if not self.is_connected:
                    self._schedule_reconnect()
                    await asyncio.sleep(DISCONNECTED_POLL_SECONDS)
                    continue
                try:
                    now = time.monotonic()
                    elapsed = now - start_time
                    snap = self._context.snapshot()
                    state = self._fsm.update(snap, now)
                    entered_post_reply = (
                        state == VtsMotionState.POST_REPLY
                        and prev_state != VtsMotionState.POST_REPLY
                    )
                    prev_state = state
                    config = self.config
                    scale = state_amplitude_scale(state)
                    speaking = state == VtsMotionState.SPEAKING

                    # ── Sway / Body + breath ──────────────────────────────
                    if self._mouse_tracking_enabled:
                        val = self._mouse_eye_tracker.read_body()
                    else:
                        val = self._sway_controller.update(
                            now, elapsed, speaking, config=config, scale=scale
                        )
                        val += self._breath_controller.value(now, config)

                    # ── Eye + Head motion ──────────────────────────────────
                    head_scale = config.head_scale / 100.0
                    eye_scale = config.eye_scale / 100.0
                    if self._mouse_tracking_enabled:
                        try:
                            cx, cy = self._cursor_pos()
                            self._mouse_eye_tracker.feed_position(cx, cy)
                        except Exception:
                            pass
                        eye_x, eye_y = self._mouse_eye_tracker.read_eye()
                        hz, hy, hx = self._mouse_eye_tracker.read_head()
                    else:
                        eye_x, eye_y = self._eye_controller.update(now)
                        hz = head_tilt_value(elapsed)
                        hy = head_nod_value(elapsed)
                        hx = head_yaw_value(elapsed)

                    # ── Batch gửi tất cả parameter mỗi tick ──────────────
                    if not self._injection_paused:
                        req = self.vts.vts_request.requestSetMultiParameterValue(
                            parameters=["LLM_Sway", "LLM_EyeX", "LLM_EyeY",
                                        "LLM_HeadZ", "LLM_HeadY", "LLM_HeadX"],
                            values=[
                                val,
                                eye_x * eye_scale, eye_y * eye_scale,
                                hz * head_scale, hy * head_scale, hx * head_scale,
                            ],
                        )
                        self._last_send_at = now
                        self._spawn_background_task(self._send_request(req))
                    elif now - self._last_send_at > 15.0:
                        # keep-alive: VTS closes idle sockets after ~30s
                        self._last_send_at = now
                        ping = self.vts.vts_request.BaseRequest(
                            "ExpressionStateRequest",
                            {"details": False, "expressionFile": ""},
                        )
                        self._spawn_background_task(self._send_request(ping))

                    # ── Micro-actions ─────────────────────────────────────
                    if self._micro_action_scheduler.update(now, state, config):
                        self._spawn_background_task(self._trigger_micro_action())

                    # ── Idle pose cycle / post-reply pose ─────────────────
                    if self._pose_cycle_scheduler.update(now, state, config):
                        if not entered_post_reply or snap.emotion in (
                            EmotionTag.JOY,
                            EmotionTag.SURPRISED,
                        ):
                            self._spawn_background_task(self._trigger_pose())

                    await asyncio.sleep(SWAY_LOOP_SLEEP_SECONDS)  # 10 Hz
                except Exception as e:
                    logger.debug("Sway loop error: %s", e)
                    await asyncio.sleep(ERROR_BACKOFF_SECONDS)
        finally:
            self._sway_running = False
```

9. `set_speaking` stays as-is for backward compatibility (GUI `/vts/speaking` endpoint will be switched to `note_speaking` in Task 7).

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_manager.py tests/test_vts_motion.py tests/test_vts_state.py -v`
Expected: all pass

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/vts_manager.py tests/test_vts_manager.py
git commit -m "feat(vts): drive sway loop from FSM, add live motion config and never-give-up reconnect"
```

---

### Task 7: VTS API endpoints (`routers/vts.py` + `_server_shared.py`)

**Files:**
- Modify: `src/mini_gpt/routers/vts.py`
- Modify: `src/mini_gpt/_server_shared.py`
- Test: `tests/test_vts_api.py`

**Interfaces:**
- Consumes: `MotionConfig` (`mini_gpt.vts_config`), `_VTS_MANAGER`/`_vts_manager`/`logger` (`_server_shared`), `effective_settings`/`save_settings_to_file` (`_server_shared`)
- Produces:
  - in `_server_shared.py`: `_load_vts_motion_config() -> MotionConfig`, `_save_vts_motion_config(config: MotionConfig) -> None` (persist under settings key `vts_motion`); `_vts_manager()` applies stored config at creation
  - `GET /vts/config` → `{"status": "ok", "config": {...}}`
  - `POST /vts/config` → validates via `MotionConfig.from_dict`; 422-style `{"status": "error", "message": ...}` on failure; persists + applies on success
  - `POST /vts/activity` `{typing: bool}` → `manager.note_user_typing`
  - `GET /vts/status` → adds `state` (str) and `config` dict; keeps `status`/`connected`
  - `/vts/speaking` now calls `manager.note_speaking`

- [ ] **Step 1: Write the failing test**

Create `tests/test_vts_api.py` (mirrors the `isolated_state` fixture pattern from `tests/test_server_api.py` — check that file's fixture and reuse it):

```python
def test_vts_config_round_trip(monkeypatch, isolated_state) -> None:
    from mini_gpt._server_shared import _VTS_MANAGER
    from mini_gpt.vts_config import MotionConfig

    manager = _VTS_MANAGER
    monkeypatch.setattr(manager, "apply_config", lambda config: None)
    with TestClient(server.app) as client:
        response = client.post(
            "/vts/config",
            json=MotionConfig(sway_amplitude_idle=18.0).to_dict(),
        )
        assert response.status_code == 200
        assert response.json()["status"] == "ok"
        saved = client.get("/vts/config").json()
    assert saved["config"]["sway_amplitude_idle"] == 18.0


def test_vts_config_rejects_invalid_values(monkeypatch, isolated_state) -> None:
    with TestClient(server.app) as client:
        response = client.post("/vts/config", json={"sway_amplitude_idle": 999.0})
        assert response.status_code == 200
        assert response.json()["status"] == "error"
        assert "sway_amplitude_idle" in response.json()["message"]


def test_vts_activity_sets_typing(monkeypatch, isolated_state) -> None:
    manager = _VTS_MANAGER
    calls: list[bool] = []
    monkeypatch.setattr(manager, "note_user_typing", lambda value: calls.append(value))
    with TestClient(server.app) as client:
        response = client.post("/vts/activity", json={"typing": True})
        assert response.status_code == 200
    assert calls == [True]


def test_vts_status_includes_state_and_config(monkeypatch, isolated_state) -> None:
    with TestClient(server.app) as client:
        body = client.get("/vts/status").json()
    assert "state" in body
    assert "config" in body
```

Note: `_VTS_MANAGER` may be `None` in tests if nothing started it — the tests above rely on the existing `isolated_state` fixture that the server suite uses; inspect `tests/test_server_api.py` for how `isolated_state` prepares the server (it typically monkeypatches `_vts_manager`). If `_VTS_MANAGER` is None there, assert against the monkeypatched `_vts_manager` return value instead, exactly like `test_vts_action_*` tests in `test_server_api.py` do.

- [ ] **Step 2: Run test to verify it fails**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_api.py -v`
Expected: FAIL — endpoints don't exist yet (404) or missing `state`/`config` keys

- [ ] **Step 3: Implement the changes**

Edit `src/mini_gpt/_server_shared.py`:

1. Add helper functions right after `_trigger_vts_catalog_codes`:

```python
def _load_vts_motion_config() -> MotionConfig:
    stored = load_settings().get("vts_motion") or {}
    try:
        return MotionConfig.from_dict(stored)
    except ValueError as error:
        logger.warning("Ignoring invalid stored vts_motion config: %s", error)
        return MotionConfig.defaults()


def _save_vts_motion_config(config: MotionConfig) -> None:
    settings = load_settings()
    settings["vts_motion"] = config.to_dict()
    save_settings_to_file(settings)
```

2. Add the `MotionConfig` import to `_server_shared.py` (from `mini_gpt.vts_config`).

3. In `_vts_manager()`, after creating the manager, apply stored config:

```python
                manager = VTSManager()
                manager.apply_config(_load_vts_motion_config())
                thread = threading.Thread(target=manager.run, name="vts-loop", daemon=True)
```

Edit `src/mini_gpt/routers/vts.py`:

4. Update imports: add `MotionConfig`, `_load_vts_motion_config`, `_save_vts_motion_config`, `effective_settings` from `.._server_shared` and `from ..vts_config import MotionConfig` is not needed (re-exported via `_server_shared` import list).

5. Replace the payload classes section with (keep `VtsActionPayload`, `VtsSpeakingPayload`; add):

```python
class VtsConfigPayload(BaseModel):
    sway_amplitude_idle: float = Field(default=10.0)
    sway_amplitude_speaking: float = Field(default=16.0)
    head_scale: float = Field(default=100.0)
    eye_scale: float = Field(default=100.0)
    micro_action_min_delay: float = Field(default=8.0)
    micro_action_max_delay: float = Field(default=22.0)
    pose_min_interval: float = Field(default=90.0)
    pose_max_interval: float = Field(default=130.0)
    breath_amplitude: float = Field(default=1.5)
    breath_rate: float = Field(default=0.15)


class VtsActivityPayload(BaseModel):
    typing: bool
```

6. Update `vts_status`:

```python
@router.get("/vts/status")
def vts_status():
    manager = _VTS_MANAGER
    connected = bool(manager is not None and manager.is_connected)
    config = manager.config.to_dict() if manager is not None else MotionConfig.defaults().to_dict()
    state = manager.current_state().value if manager is not None else "idle"
    return {"status": "ok", "connected": connected, "state": state, "config": config}
```

7. Add config endpoints before `/vts/action`:

```python
@router.get("/vts/config")
def vts_config_get():
    return {"status": "ok", "config": _load_vts_motion_config().to_dict()}


@router.post("/vts/config")
def vts_config_save(payload: VtsConfigPayload):
    try:
        config = MotionConfig.from_dict(payload.model_dump())
    except ValueError as error:
        return {"status": "error", "message": str(error)}
    _save_vts_motion_config(config)
    manager = _VTS_MANAGER
    if manager is not None:
        manager.apply_config(config)
    return {"status": "ok", "config": config.to_dict()}
```

8. Add activity endpoint:

```python
@router.post("/vts/activity")
def vts_activity(payload: VtsActivityPayload):
    manager = _VTS_MANAGER
    if manager is not None:
        manager.note_user_typing(payload.typing)
    return {"status": "ok"}
```

9. Update `vts_speaking` to use the context wrapper:

```python
    manager.note_speaking(payload.speaking)
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_api.py tests/test_server_api.py -v`
Expected: all pass

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/routers/vts.py src/mini_gpt/_server_shared.py tests/test_vts_api.py
git commit -m "feat(api): vts config/activity endpoints, status state+config, persisted motion settings"
```

---

### Task 8: Chat & observation plumbing (`chat.py`, `observation.py`, `vts_catalog.py`)

**Files:**
- Modify: `src/mini_gpt/vts_catalog.py`
- Modify: `src/mini_gpt/routers/chat.py`
- Modify: `src/mini_gpt/routers/observation.py`
- Test: `tests/test_vts_catalog_actions.py` (extend)

**Interfaces:**
- Consumes: `extract_reply_emotion`/`EmotionTag` (`mini_gpt.vts_emotion`), `parse_catalog_reply` (`mini_gpt.vts_catalog`)
- Produces:
  - `split_reply_metadata(raw_reply: str, catalog: VtsCatalog | None) -> tuple[str, tuple[str, ...], EmotionTag]` in `vts_catalog.py` — strips `[[EMO:...]]` first, then `[[VTS:...]]`, returns `(clean_reply, codes, emotion)`
  - `chat.py`: call `manager.note_generating(True)` before generation, `manager.note_generating(False)` + `manager.note_emotion(emotion)` after
  - `observation.py`: same pattern

- [ ] **Step 1: Write the failing test**

Append to `tests/test_vts_catalog_actions.py`:

```python
from mini_gpt.vts_catalog import split_reply_metadata
from mini_gpt.vts_emotion import EmotionTag


def test_split_reply_metadata_strips_emo_then_vts_codes():
    catalog = build_catalog(
        model_id="model-1",
        model_name="Ru",
        hotkeys=[{"name": "Happy", "hotkeyID": "happy-id"}],
        expressions=[],
    )

    clean, codes, emotion = split_reply_metadata(
        "Hay quá! [[EMO:joy]] [[VTS:vts_01]]", catalog
    )

    assert clean == "Hay quá!"
    assert codes == ("vts_01",)
    assert emotion == EmotionTag.JOY


def test_split_reply_metadata_falls_back_to_heuristic():
    clean, codes, emotion = split_reply_metadata("Em buồn quá", None)
    assert clean == "Em buồn quá"
    assert codes == ()
    assert emotion == EmotionTag.SAD
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_catalog_actions.py -v`
Expected: FAIL — `ImportError: cannot import name 'split_reply_metadata'`

- [ ] **Step 3: Implement the changes**

Edit `src/mini_gpt/vts_catalog.py`:

1. Add import + function after `parse_catalog_reply`:

```python
from .vts_emotion import EmotionTag, extract_reply_emotion


def split_reply_metadata(
    raw_reply: str, catalog: VtsCatalog | None
) -> tuple[str, tuple[str, ...], EmotionTag]:
    """Strip emotion marker first, then VTS codes; return both plus emotion."""
    reply, emotion = extract_reply_emotion(raw_reply)
    clean, codes = parse_catalog_reply(reply, catalog)
    return clean, codes, emotion
```

Edit `src/mini_gpt/routers/chat.py`:

2. Add imports (near the `from .._server_shared import parse_catalog_reply` line — replace it):

```python
            from .._server_shared import split_reply_metadata
```

3. In `run_generation`, right after the `manager`/`catalog` block (after `catalog = None` … try/except), add:

```python
            if manager is not None:
                manager.note_generating(True)
```

4. Replace:

```python
            from .._server_shared import parse_catalog_reply
            full_reply, codes = parse_catalog_reply(raw_reply, catalog)
```

with:

```python
            full_reply, codes, emotion = split_reply_metadata(raw_reply, catalog)
            if manager is not None:
                manager.note_generating(False)
                manager.note_emotion(emotion)
```

Edit `src/mini_gpt/routers/observation.py`:

5. Add imports: `split_reply_metadata` from `.._server_shared` (re-export there) or directly `from ..vts_catalog import split_reply_metadata`. Replace:

```python
        cleaned, codes = parse_catalog_reply(raw_reply, catalog)
```

with:

```python
        cleaned, codes, emotion = split_reply_metadata(raw_reply, catalog)
        manager_ = _VTS_MANAGER
        if manager_ is not None:
            manager_.note_generating(False)
            manager_.note_emotion(emotion)
```

6. In `_server_shared.py`, re-export `split_reply_metadata` by importing it next to `parse_catalog_reply` so `chat.py`/`observation.py` can import it from `.._server_shared`:

```python
from mini_gpt.vts_catalog import parse_catalog_reply, split_reply_metadata
```

(verify the existing import line and extend it)

Note: for `observation.py`, also call `manager_.note_generating(True)` before `generate_screen_reaction` and `note_generating(False)` in the success path (the reaction is a generation).

- [ ] **Step 4: Run tests to verify they pass**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_vts_catalog_actions.py tests/test_server_api.py -v`
Expected: all pass

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/vts_catalog.py src/mini_gpt/routers/chat.py src/mini_gpt/routers/observation.py src/mini_gpt/_server_shared.py tests/test_vts_catalog_actions.py
git commit -m "feat(chat): feed generating/emotion context into VTS motion FSM"
```

---

### Task 9: WinUI3 GUI — sliders, status, typing push

**Files:**
- Modify: `gui-winui3/Models/Models.cs`
- Modify: `gui-winui3/Services/ApiService.cs`
- Modify: `gui-winui3/Views/VoiceView.xaml`
- Modify: `gui-winui3/Views/VoiceView.xaml.cs`
- Modify: `gui-winui3/Views/ChatView.xaml.cs`
- Verify: `dotnet build` in `gui-winui3`

**Interfaces:**
- Consumes: endpoints from Task 7 (`GET/POST /vts/config`, `POST /vts/activity`, `GET /vts/status`)
- Produces (C#):
  - `Models.cs`: `VtsConfigDto` (10 double props, JSON names = snake_case), `VtsStatusDto` gains `State` (string) and `Config` (VtsConfigDto?)
  - `ApiService`: `GetVtsConfigAsync()`, `SaveVtsConfigAsync(VtsConfigDto)`, `PushVtsActivityAsync(bool)`, `GetVtsStatusAsync() -> VtsStatusDto?` (extend existing)

- [ ] **Step 1: Add DTOs + API methods (no test runner; verify by build)**

In `Models.cs`, after the existing `VtsStatusDto` class, add:

```csharp
public class VtsConfigDto
{
    [JsonPropertyName("sway_amplitude_idle")]
    public double SwayAmplitudeIdle { get; set; } = 10.0;
    [JsonPropertyName("sway_amplitude_speaking")]
    public double SwayAmplitudeSpeaking { get; set; } = 16.0;
    [JsonPropertyName("head_scale")]
    public double HeadScale { get; set; } = 100.0;
    [JsonPropertyName("eye_scale")]
    public double EyeScale { get; set; } = 100.0;
    [JsonPropertyName("micro_action_min_delay")]
    public double MicroActionMinDelay { get; set; } = 8.0;
    [JsonPropertyName("micro_action_max_delay")]
    public double MicroActionMaxDelay { get; set; } = 22.0;
    [JsonPropertyName("pose_min_interval")]
    public double PoseMinInterval { get; set; } = 90.0;
    [JsonPropertyName("pose_max_interval")]
    public double PoseMaxInterval { get; set; } = 130.0;
    [JsonPropertyName("breath_amplitude")]
    public double BreathAmplitude { get; set; } = 1.5;
    [JsonPropertyName("breath_rate")]
    public double BreathRate { get; set; } = 0.15;
}
```

Check the existing `VtsStatusDto` in `Models.cs` (around the `ObservationMetricsDto`) and add two optional properties:

```csharp
[JsonPropertyName("state")]
public string? State { get; set; }
[JsonPropertyName("config")]
public VtsConfigDto? Config { get; set; }
```

In `ApiService.cs`, next to the existing `GetVtsStatusAsync`/`SetVtsSpeakingAsync` methods, add:

```csharp
public async Task<VtsConfigDto?> GetVtsConfigAsync()
{
    try
    {
        var result = await _httpClient.GetFromJsonAsync<VtsConfigResponse>($"{_baseUrl}/vts/config");
        return result?.Config;
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[ApiService] GetVtsConfigAsync failed: {ex.Message}");
        return null;
    }
}

public async Task<bool> SaveVtsConfigAsync(VtsConfigDto config)
{
    try
    {
        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/vts/config", config);
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[ApiService] SaveVtsConfigAsync failed: {ex.Message}");
        return false;
    }
}

public async Task PushVtsActivityAsync(bool typing)
{
    try
    {
        await _httpClient.PostAsJsonAsync($"{_baseUrl}/vts/activity", new { typing });
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[ApiService] PushVtsActivityAsync failed: {ex.Message}");
    }
}
```

Add the small response wrapper class next to the DTOs:

```csharp
public class VtsConfigResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
    [JsonPropertyName("config")]
    public VtsConfigDto? Config { get; set; }
}
```

- [ ] **Step 2: VoiceView sliders + status (XAML + code-behind)**

In `VoiceView.xaml`, inside the VTS card `StackPanel` (after the existing manual action buttons `StackPanel`), add:

```xml
<TextBlock Text="Cường độ chuyển động (áp dụng ngay):" FontSize="13" Foreground="#AAAAAA" Margin="0,16,0,0"/>
<Grid ColumnDefinitions="*,Auto">
    <TextBlock Grid.Column="0" Text="Sway (thân)" VerticalAlignment="Center" Foreground="#DDDDDD"/>
    <Slider x:Name="SwaySlider" Grid.Column="1" Minimum="0" Maximum="100" StepFrequency="1" Value="100" Width="240" ValueChanged="OnVtsConfigSliderChanged"/>
</Grid>
<Grid ColumnDefinitions="*,Auto">
    <TextBlock Grid.Column="0" Text="Đầu" VerticalAlignment="Center" Foreground="#DDDDDD"/>
    <Slider x:Name="HeadSlider" Grid.Column="1" Minimum="0" Maximum="200" StepFrequency="1" Value="100" Width="240" ValueChanged="OnVtsConfigSliderChanged"/>
</Grid>
<Grid ColumnDefinitions="*,Auto">
    <TextBlock Grid.Column="0" Text="Mắt" VerticalAlignment="Center" Foreground="#DDDDDD"/>
    <Slider x:Name="EyeSlider" Grid.Column="1" Minimum="0" Maximum="200" StepFrequency="1" Value="100" Width="240" ValueChanged="OnVtsConfigSliderChanged"/>
</Grid>
<Grid ColumnDefinitions="*,Auto">
    <TextBlock Grid.Column="0" Text="Tần suất micro-action" VerticalAlignment="Center" Foreground="#DDDDDD"/>
    <Slider x:Name="MicroSlider" Grid.Column="1" Minimum="0" Maximum="100" StepFrequency="1" Value="100" Width="240" ValueChanged="OnVtsConfigSliderChanged"/>
</Grid>
<Grid ColumnDefinitions="*,Auto">
    <TextBlock Grid.Column="0" Text="Tần suất pose" VerticalAlignment="Center" Foreground="#DDDDDD"/>
    <Slider x:Name="PoseSlider" Grid.Column="1" Minimum="0" Maximum="100" StepFrequency="1" Value="100" Width="240" ValueChanged="OnVtsConfigSliderChanged"/>
</Grid>
```

In `VoiceView.xaml`, replace the status line inside the VTS card with:

```xml
<StackPanel Grid.Column="0" Spacing="4">
    <TextBlock Text="VTube Studio Avatar Live2D" FontSize="18" FontFamily="Segoe UI Variable Display" FontWeight="SemiBold" Foreground="#EAEAEA"/>
    <TextBlock x:Name="VtsStatusText" Text="Trạng thái: Chưa kết nối" Foreground="#888888"/>
    <TextBlock x:Name="VtsStateText" Text="" Foreground="#666666" FontSize="12"/>
</StackPanel>
```

In `VoiceView.xaml.cs`:

- add fields + debounce + poll timers in the class:

```csharp
private DispatcherTimer? _vtsConfigDebounce;
private DispatcherTimer? _vtsStatusTimer;
private bool _loadingVtsConfig;
```

- in `Initialize`, after existing setup, add:

```csharp
_vtsConfigDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
_vtsConfigDebounce.Tick += async (_, __) =>
{
    _vtsConfigDebounce.Stop();
    await SaveVtsConfigAsync();
};

_vtsStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
_vtsStatusTimer.Tick += async (_, __) => await PollVtsStatusAsync();
_vtsStatusTimer.Start();
_ = LoadVtsConfigAsync();
```

- add the handlers + helpers (slider mapping per spec: Sway lerp idle 5→30 / speaking 5→40; Head = percent; Eye = percent; Micro = delay lerp 100% → 8/22, 0% → 40/90; Pose = interval lerp 100% → 90/130, 0% → 300/600):

```csharp
private void OnVtsConfigSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
{
    if (_loadingVtsConfig) return;
    _vtsConfigDebounce?.Stop();
    _vtsConfigDebounce?.Start();
}

private async Task LoadVtsConfigAsync()
{
    if (_apiService == null) return;
    _loadingVtsConfig = true;
    var config = await _apiService.GetVtsConfigAsync();
    if (config != null)
    {
        SwaySlider.Value = SwayAmplitudeToPercent(config.SwayAmplitudeIdle);
        HeadSlider.Value = config.HeadScale;
        EyeSlider.Value = config.EyeScale;
        MicroSlider.Value = MicroDelayToPercent(config.MicroActionMinDelay);
        PoseSlider.Value = PoseIntervalToPercent(config.PoseMinInterval);
    }
    _loadingVtsConfig = false;
}

private async Task SaveVtsConfigAsync()
{
    if (_apiService == null) return;
    var config = new VtsConfigDto
    {
        SwayAmplitudeIdle = SwayPercentToAmplitude(SwaySlider.Value, idle: true),
        SwayAmplitudeSpeaking = SwayPercentToAmplitude(SwaySlider.Value, idle: false),
        HeadScale = HeadSlider.Value,
        EyeScale = EyeSlider.Value,
        MicroActionMinDelay = MicroPercentToDelay(MicroSlider.Value, min: true),
        MicroActionMaxDelay = MicroPercentToDelay(MicroSlider.Value, min: false),
        PoseMinInterval = PosePercentToInterval(PoseSlider.Value, min: true),
        PoseMaxInterval = PosePercentToInterval(PoseSlider.Value, min: false),
        BreathAmplitude = 1.5,
        BreathRate = 0.15,
    };
    await _apiService.SaveVtsConfigAsync(config);
}

private static double SwayPercentToAmplitude(double percent, bool idle)
{
    double min = idle ? 5.0 : 5.0;
    double max = idle ? 30.0 : 40.0;
    return min + (max - min) * percent / 100.0;
}

private static double SwayAmplitudeToPercent(double amplitude)
{
    if (amplitude <= 5.0) return 0.0;
    if (amplitude >= 30.0) return 100.0;
    return (amplitude - 5.0) / 25.0 * 100.0;
}

private static double MicroPercentToDelay(double percent, bool min)
{
    double high = min ? 40.0 : 90.0;
    double low = min ? 8.0 : 22.0;
    return low + (high - low) * (100.0 - percent) / 100.0;
}

private static double MicroDelayToPercent(double delay)
{
    if (delay <= 8.0) return 100.0;
    if (delay >= 40.0) return 0.0;
    return 100.0 - (delay - 8.0) / 32.0 * 100.0;
}

private static double PosePercentToInterval(double percent, bool min)
{
    double high = min ? 300.0 : 600.0;
    double low = min ? 90.0 : 130.0;
    return low + (high - low) * (100.0 - percent) / 100.0;
}

private static double PoseIntervalToPercent(double interval)
{
    if (interval <= 90.0) return 100.0;
    if (interval >= 300.0) return 0.0;
    return 100.0 - (interval - 90.0) / 210.0 * 100.0;
}

private async Task PollVtsStatusAsync()
{
    if (_apiService == null) return;
    var status = await _apiService.GetVtsStatusAsync();
    if (status == null) return;
    VtsStatusText.Text = status.Connected ? "Trạng thái: Đã kết nối" : "Trạng thái: Chưa kết nối";
    VtsStateText.Text = status.Connected && !string.IsNullOrEmpty(status.State)
        ? "Motion: " + status.State
        : "";
}
```

- [ ] **Step 3: ChatView typing push**

In `ChatView.xaml.cs`, add fields:

```csharp
private DispatcherTimer? _typingDebounce;
private bool _typingPushed;
```

In `Initialize` (find it — the method that wires `ChatInputTextBox`), add:

```csharp
ChatInputTextBox.TextChanged += OnChatInputTyping;
_typingDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
_typingDebounce.Tick += (_, __) =>
{
    _typingDebounce.Stop();
    if (!string.IsNullOrEmpty(ChatInputTextBox.Text) && !_typingPushed)
    {
        _typingPushed = true;
        _ = _apiService?.PushVtsActivityAsync(true);
    }
};
```

Add the handler:

```csharp
private void OnChatInputTyping(object sender, TextChangedEventArgs e)
{
    if (string.IsNullOrEmpty(ChatInputTextBox.Text))
    {
        _typingDebounce?.Stop();
        if (_typingPushed)
        {
            _typingPushed = false;
            _ = _apiService?.PushVtsActivityAsync(false);
        }
        return;
    }
    _typingDebounce?.Stop();
    _typingDebounce?.Start();
}
```

In `OnSendClick`, after `ChatInputTextBox.Text = string.Empty;`, add:

```csharp
if (_typingPushed)
{
    _typingPushed = false;
    _ = _apiService?.PushVtsActivityAsync(false);
}
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build RuApp.csproj -c Debug -r win-x64 -v q`
Expected: Build succeeded, 0 Warning(s), 0 Error(s)

- [ ] **Step 5: Run full backend suite once**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest -q`
Expected: all pass (299 existing + new tests)

- [ ] **Step 6: Commit**

```bash
git add gui-winui3/Models/Models.cs gui-winui3/Services/ApiService.cs gui-winui3/Views/VoiceView.xaml gui-winui3/Views/VoiceView.xaml.cs gui-winui3/Views/ChatView.xaml.cs
git commit -m "feat(gui): VTS motion sliders, live state label, typing activity push"
```

---

## Self-Review Notes

- Spec §3 (states/transitions) → Task 4; §4.1 → Task 4; §4.2 → Task 3; §4.3 → Task 1; §4.4 → Task 6; §4.5 → Tasks 2+5; §5.1 → Task 7; §5.2 → Task 8; §5.3 → Task 9; §6 → Tasks 2/7 (validation), 1/8 (emotion fallback), 6 (reconnect/disconnect reset), 9 (GUI debounce); §7 → Tasks 1-6 (unit), 7 (API), 9 (build); §8 deferred.
- Slider mapping formulas in Task 9 implement the spec's lerp rules; `MicroDelayToPercent`/`PoseIntervalToPercent` invert them for loading.
- `set_speaking` kept for compatibility; `/vts/speaking` now routes through `note_speaking` so FSM input is single-sourced.
