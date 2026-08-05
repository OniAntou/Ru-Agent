"""Pure motion policy helpers for the Ru VTube Studio integration."""

from __future__ import annotations

import math
import random
from typing import Iterable

from .vts_config import MotionConfig
from .vts_state import VtsMotionState


# ── Sway amplitudes ───────────────────────────────────────────────────────
IDLE_SWAY_AMPLITUDE = 10.0       # Biên độ khi không nói (từ 5 → 10)
SPEAKING_SWAY_AMPLITUDE = 16.0   # Biên độ khi nói (từ 14 → 16)


def sway_value(
    elapsed: float,
    speaking: bool,
    *,
    idle_amplitude: float = IDLE_SWAY_AMPLITUDE,
    speaking_amplitude: float = SPEAKING_SWAY_AMPLITUDE,
) -> float:
    """Return a bounded, multi-layered sway target.

    4 sine-wave layers with incommensurate frequencies produce organic,
    non-repeating motion.  A slow amplitude-modulation envelope adds
    subtle "surges" that keep the movement feeling alive.
    """
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


# ── Idle head motion ─────────────────────────────────────────────────────
# Slow head-movement waves injected into VRChat-style tracking parameters.
# VRM models map these to head-bone rotation; amplitude ±8 is clearly
# visible without being distracting.
_HEAD_TILT_RATE = 0.15      # ~1 chu kỳ mỗi 6.7s
_HEAD_NOD_RATE  = 0.10      # ~1 chu kỳ mỗi 10s
_HEAD_YAW_RATE  = 0.06      # ~1 chu kỳ mỗi 16.7s
_HEAD_TILT_AMPLITUDE = 8.0
_HEAD_NOD_AMPLITUDE  = 6.0
_HEAD_YAW_AMPLITUDE  = 4.0


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


def head_tilt_value(elapsed: float) -> float:
    """Roll (nghiêng đầu) → PARAM_ANGLE_Z."""
    return _HEAD_TILT_AMPLITUDE * math.sin(float(elapsed) * _HEAD_TILT_RATE * 6.2832 + 0.5)


def head_nod_value(elapsed: float) -> float:
    """Pitch (gật/gù) → PARAM_ANGLE_Y."""
    return _HEAD_NOD_AMPLITUDE * math.sin(float(elapsed) * _HEAD_NOD_RATE * 6.2832 + 2.1)


def head_yaw_value(elapsed: float) -> float:
    """Yaw (quay trái/phải) → PARAM_ANGLE_X."""
    return _HEAD_YAW_AMPLITUDE * math.sin(float(elapsed) * _HEAD_YAW_RATE * 6.2832 + 3.4)


class OrganicSwayController:
    """Ease the live sway value toward each layered target."""

    def __init__(self) -> None:
        self._value = 0.0
        self._last_now: float | None = None
        self._drift = 0.0
        self._drift_target = 0.0
        self._next_drift_at: float | None = None

    def reset(self) -> None:
        self._value = 0.0
        self._last_now = None
        self._drift = 0.0
        self._drift_target = 0.0
        self._next_drift_at = None

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


def is_safe_micro_action(label: str, identifier: str) -> bool:
    """Allow only small, reversible face/ear/tail/head motions."""

    searchable = f"{label} {identifier}".casefold()
    blocked_terms = (
        "^",
        "pout",
        "blush",
        "\u8138\u7ea2",
        "\u8863",
        "\u670d",
        "\u80f8",
        "\u817f",
        "\u978b",
    )
    if any(term.casefold() in searchable for term in blocked_terms):
        return False

    safe_terms = (
        "blink",
        "eye",
        "close",
        "restore",
        "ear",
        "tail",
        "head",
        "nod",
        "tilt",
        "\u773c",
        "\u95ed",
        "\u6062\u590d",
        "\u8033",
        "\u5c3e",
        "\u5934",
    )
    return any(term.casefold() in searchable for term in safe_terms)


# When idle, exclude tail-wag / body-level motions — keep it to subtle
# face/ear/head-only gestures.
_IDLE_ONLY_SAFE_TERMS = (
    "blink", "eye", "close", "restore",
    "ear", "head", "nod", "tilt",
    "\u773c", "\u95ed", "\u6062\u590d", "\u8033", "\u5934",
)


def is_idle_safe_micro_action(label: str, identifier: str) -> bool:
    """Stricter filter for idle periods — no tail, no body."""
    searchable = f"{label} {identifier}".casefold()
    return any(term.casefold() in searchable for term in _IDLE_ONLY_SAFE_TERMS)


def safe_micro_action_entries(entries: Iterable[object]) -> list[object]:
    """Filter catalog entries using the conservative micro-action policy."""

    return [
        entry
        for entry in entries
        if is_safe_micro_action(
            str(getattr(entry, "label", "")),
            str(getattr(entry, "identifier", "")),
        )
    ]


class MicroActionScheduler:
    """Schedule ambient micro-actions during both speaking and idle periods.

    When *speaking*  → interval 10-22 s (livelier).
    When *idle*      → interval  8-22 s (micro-gestures keep the model
                          looking alive without being distracting).
    Resetting sets the timer back to ``None`` so the next call picks a
    fresh delay from the appropriate range.
    """

    def __init__(self, random_source: object | None = None) -> None:
        self.random_source = random_source or random
        self.next_due_at: float | None = None

    def reset(self) -> None:
        self.next_due_at = None

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


# ── Eye motion (nhìn ngó lung tung) ─────────────────────────────────────
# Model nhìn theo target ngẫu nhiên: giữ 2-6s rồi chuyển hướng.
# Dùng ease-in/out để mắt di chuyển mượt, không giật.
_EYE_RANGE_X = 8.0        # ±8 trái/phải
_EYE_RANGE_Y = 5.0        # ±5 lên/xuống
_EYE_HOLD_MIN = 2.0       # giữ tối thiểu 2s
_EYE_HOLD_MAX = 6.0       # giữ tối đa 6s
_EYE_EASE_IN = 0.35       # thời gian ease đến target (sec)


class EyeMotionController:
    """Simulates natural gaze shifts — random targets with smooth easing.

    - Picks a random (x, y) target within range
    - Eases toward it over ~0.35 s
    - Holds for 2-6 s
    - Repeats indefinitely
    """

    def __init__(self, random_source: object | None = None) -> None:
        self.random_source = random_source or random
        self._x = 0.0
        self._y = 0.0
        self._target_x = 0.0
        self._target_y = 0.0
        self._last_switch: float | None = None
        self._switch_every: float = 4.0
        self._ease_start: float | None = None

    def reset(self) -> None:
        self._x = 0.0
        self._y = 0.0
        self._target_x = 0.0
        self._target_y = 0.0
        self._last_switch = None
        self._ease_start = None

    def update(self, now: float) -> tuple[float, float]:
        """Return (eye_x, eye_y) — call every sway_loop tick (100 ms)."""

        if self._last_switch is None:
            self._last_switch = now
            self._switch_every = self.random_source.uniform(_EYE_HOLD_MIN, _EYE_HOLD_MAX)
            return (self._x, self._y)

        elapsed_since_switch = now - self._last_switch

        # Chọn target mới sau khi hết hold time
        if elapsed_since_switch >= self._switch_every:
            self._target_x = self.random_source.uniform(-_EYE_RANGE_X, _EYE_RANGE_X)
            self._target_y = self.random_source.uniform(-_EYE_RANGE_Y, _EYE_RANGE_Y)
            self._last_switch = now
            self._ease_start = now
            self._switch_every = self.random_source.uniform(_EYE_HOLD_MIN, _EYE_HOLD_MAX)
            return (self._x, self._y)

        # Ease phase: mượt về target trong _EYE_EASE_IN giây
        if self._ease_start is not None:
            t = (now - self._ease_start) / _EYE_EASE_IN
            if t >= 1.0:
                self._x = self._target_x
                self._y = self._target_y
                self._ease_start = None
            else:
                # Smoothstep lerp đầy đủ: t=1 đạt target chính xác, không nhảy
                s = t * t * (3 - 2 * t)
                self._x += (self._target_x - self._x) * s
                self._y += (self._target_y - self._y) * s

        return (self._x, self._y)


class MouseEyeTracker:
    """Tracks cursor and produces smooth **eye** + **head** parameter values.

    When cursor-follow is active, the model's eyes respond quickly (high
    smoothing) while the head follows more slowly and with a smaller range
    — mimicking natural gaze: *eyes lead, head follows*.
    """

    # ── Eye ─────────────────────────────────────────────────────────────
    _EYE_SMOOTH = 0.98         # gần realtime
    _EYE_SCALE_X = 9.5         # khớp LLM_EyeX max ±10
    _EYE_SCALE_Y = 5.5         # khớp LLM_EyeY max ±6

    # ── Head ────────────────────────────────────────────────────────────
    _HEAD_SMOOTH = 0.92        # realtime
    _HEAD_SCALE_Z = 12.0       # nghiêng ±12 (LLM_HeadZ max ±15)
    _HEAD_SCALE_Y = 12.0       # gật ±12 (LLM_HeadY max ±15)
    _HEAD_SCALE_X = 9.5        # quay ±9.5 (LLM_HeadX max ±10)

    # ── Body (LLM_Sway) ─────────────────────────────────────────────────
    _BODY_SMOOTH = 0.92        # realtime
    _BODY_SCALE = 14.0         # range ±14 (LLM_Sway max ±30)

    def __init__(self) -> None:
        # eye state
        self._ex = 0.0
        self._ey = 0.0
        # head state
        self._hz = 0.0
        self._hy = 0.0
        self._hx = 0.0
        # body state (LLM_Sway)
        self._body = 0.0
        self._screen_w: int = 1920
        self._screen_h: int = 1080

    def start(self) -> None:
        """Cache screen dimensions (call once before first *feed_position*)."""
        try:
            import pyautogui
            self._screen_w, self._screen_h = pyautogui.size()
        except Exception:
            try:
                import ctypes
                user32 = ctypes.windll.user32
                self._screen_w = user32.GetSystemMetrics(0)
                self._screen_h = user32.GetSystemMetrics(1)
            except Exception:
                pass  # keep defaults

    def feed_position(self, cursor_x: int, cursor_y: int) -> None:
        """Ingest a cursor position and update eased eye + head values."""

        nx = max(-1.0, min(1.0, (cursor_x / self._screen_w) * 2 - 1))   # -1 .. 1
        ny = max(-1.0, min(1.0, (cursor_y / self._screen_h) * 2 - 1))   # -1 .. 1 (top = -1)

        # ── Eye — quick, full range ──────────────────────────────────────
        tx = -nx * self._EYE_SCALE_X                      # đảo X: chuột trái → mắt nhìn trái
        ty = -ny * self._EYE_SCALE_Y                      # flip Y: up = +
        self._ex += (tx - self._ex) * self._EYE_SMOOTH
        self._ey += (ty - self._ey) * self._EYE_SMOOTH

        # ── Head — slow, smaller range, lags behind eyes ────────────────
        tz = nx * self._HEAD_SCALE_Z                      # nghiêng
        ty_h = -ny * self._HEAD_SCALE_Y                   # gật
        tx_h = nx * self._HEAD_SCALE_X                    # quay
        self._hz += (tz - self._hz) * self._HEAD_SMOOTH
        self._hy += (ty_h - self._hy) * self._HEAD_SMOOTH
        self._hx += (tx_h - self._hx) * self._HEAD_SMOOTH

        # ── Body (LLM_Sway) — chậm, scale nhỏ ──────────────────────────
        tb = -nx * self._BODY_SCALE                        # đảo X
        self._body += (tb - self._body) * self._BODY_SMOOTH

    def read_eye(self) -> tuple[float, float]:
        """Return eased (eye_x, eye_y)."""
        return (self._ex, self._ey)

    def read_head(self) -> tuple[float, float, float]:
        """Return eased (head_z, head_y, head_x) — tilt / nod / yaw."""
        return (self._hz, self._hy, self._hx)

    def read_body(self) -> float:
        """Return eased body sway value (LLM_Sway)."""
        return self._body

    def reset(self) -> None:
        self._ex = 0.0
        self._ey = 0.0
        self._hz = 0.0
        self._hy = 0.0
        self._hx = 0.0
        self._body = 0.0


# ── Idle pose cycle ─────────────────────────────────────────────────────
# Hotkey body-pose terms — hand, arm, wave, etc. (ngược với micro-action
# chỉ face/head)
_POSE_SAFE_TERMS = (
    "hand", "arm", "肩", "手", "指",
    "wave", "bye", "举手", "挥手",
    "fold", "cross", "腕",
    "point", "指差",
    "hip", "腰",
    "salute",
    "peace",
    "heart", "ハート",
)


def is_body_pose_action(label: str, identifier: str) -> bool:
    """Return ``True`` when an entry looks like a full-body pose hotkey."""
    searchable = f"{label} {identifier}".casefold()
    return any(term.casefold() in searchable for term in _POSE_SAFE_TERMS)


def body_pose_entries(entries: Iterable[object]) -> list[object]:
    """Filter catalog hotkeys that qualify as body poses."""
    return [
        entry
        for entry in entries
        if entry.kind == "hotkey"
        and is_body_pose_action(
            str(getattr(entry, "label", "")),
            str(getattr(entry, "identifier", "")),
        )
    ]


class PoseCycleScheduler:
    """Triggers a random body pose on POST_REPLY entry and on idle intervals."""

    def __init__(self, random_source: object | None = None) -> None:
        self.random_source = random_source or random
        self._next_at: float | None = None
        self._last_pose_code: str | None = None
        self._post_reply_armed = False

    def reset(self) -> None:
        self._next_at = None
        self._last_pose_code = None
        self._post_reply_armed = False

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

    def pick(self, candidates: list[object]) -> object | None:
        if not candidates:
            return None
        if self._last_pose_code and len(candidates) > 1:
            others = [e for e in candidates if e.code != self._last_pose_code]
            if others:
                chosen = self.random_source.choice(others)
                self._last_pose_code = chosen.code
                return chosen
        chosen = self.random_source.choice(candidates)
        self._last_pose_code = chosen.code
        return chosen

    def _roll_interval(self, config: MotionConfig) -> float:
        return float(
            self.random_source.uniform(config.pose_min_interval, config.pose_max_interval)
        )
