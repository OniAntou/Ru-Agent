"""Pure motion policy helpers for the Ru VTube Studio integration."""

from __future__ import annotations

import math
import random
from typing import Iterable


# ── Sway amplitudes ───────────────────────────────────────────────────────
IDLE_SWAY_AMPLITUDE = 10.0       # Biên độ khi không nói (từ 5 → 10)
SPEAKING_SWAY_AMPLITUDE = 16.0   # Biên độ khi nói (từ 14 → 16)

# ── Micro-actions ─────────────────────────────────────────────────────────
SPEAKING_MICRO_ACTION_MIN_DELAY = 10.0
SPEAKING_MICRO_ACTION_MAX_DELAY = 22.0
# Idle: dày hơn để trông sống động — cử chỉ nhỏ liên tục giúp model
# không bị đứng hình dù không nói.
IDLE_MICRO_ACTION_MIN_DELAY = 8.0
IDLE_MICRO_ACTION_MAX_DELAY = 22.0


def sway_value(elapsed: float, speaking: bool) -> float:
    """Return a bounded, multi-layered sway target.

    4 sine-wave layers with incommensurate frequencies produce organic,
    non-repeating motion.  A slow amplitude-modulation envelope adds
    subtle "surges" that keep the movement feeling alive.
    """
    amplitude = SPEAKING_SWAY_AMPLITUDE if speaking else IDLE_SWAY_AMPLITUDE
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
_HEAD_TILT_RATE = 0.15      # ~1 chu kỳ mỗi 42s
_HEAD_NOD_RATE  = 0.10      # ~1 chu kỳ mỗi 63s
_HEAD_YAW_RATE  = 0.06      # ~1 chu kỳ mỗi 105s
_HEAD_TILT_AMPLITUDE = 8.0
_HEAD_NOD_AMPLITUDE  = 6.0
_HEAD_YAW_AMPLITUDE  = 4.0


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

    def update(self, now: float, elapsed: float, speaking: bool) -> float:
        if self._last_now is None:
            self._last_now = now
            return self._value

        delta = max(0.0, float(now) - self._last_now)
        amplitude = SPEAKING_SWAY_AMPLITUDE if speaking else IDLE_SWAY_AMPLITUDE
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
        target = sway_value(elapsed, speaking) + self._drift
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

    def update(self, now: float, speaking: bool) -> bool:
        """Return ``True`` when a micro-action is due."""

        if self.next_due_at is None:
            self.next_due_at = now + self._next_delay(speaking)
            return False

        if now < self.next_due_at:
            return False

        self.next_due_at = now + self._next_delay(speaking)
        return True

    def _next_delay(self, speaking: bool) -> float:
        if speaking:
            return float(
                self.random_source.uniform(
                    SPEAKING_MICRO_ACTION_MIN_DELAY,
                    SPEAKING_MICRO_ACTION_MAX_DELAY,
                )
            )
        return float(
            self.random_source.uniform(
                IDLE_MICRO_ACTION_MIN_DELAY,
                IDLE_MICRO_ACTION_MAX_DELAY,
            )
        )


# ── Eye motion (nhìn ngó lung tung) ─────────────────────────────────────
# Model nhìn theo target ngẫu nhiên: giữ 2-6s rồi chuyển hướng.
# Dùng ease-in/out để mắt di chuyển mượt, không giật.
_EYE_RANGE_X = 8.0        # ±8 trái/phải
_EYE_RANGE_Y = 5.0        # ±5 lên/xuống
_EYE_HOLD_MIN = 2.0       # giữ tối thiểu 2s
_EYE_HOLD_MAX = 6.0       # giữ tối đa 6s
_EYE_EASE_IN = 0.35       # thời gian ease đến target (sec)
_EYE_BLINK_COOLDOWN = 4.0 # không chọn target mới quá nhanh sau blink


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
        """Return (eye_x, eye_y) — call every sway_loop tick (50 ms)."""

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
                # Smoothstep ease
                s = t * t * (3 - 2 * t)
                self._x += (self._target_x - self._x) * s * 0.15
                self._y += (self._target_y - self._y) * s * 0.15

        return (self._x, self._y)


class MouseEyeTracker:
    """Tracks cursor and produces smooth **eye** + **head** parameter values.

    When cursor-follow is active, the model's eyes respond quickly (high
    smoothing) while the head follows more slowly and with a smaller range
    — mimicking natural gaze: *eyes lead, head follows*.
    """

    # ── Eye ─────────────────────────────────────────────────────────────
    _EYE_SMOOTH = 0.98         # gần realtime
    _EYE_SCALE_X = 12.0        # range ±12
    _EYE_SCALE_Y = 12.0

    # ── Head ────────────────────────────────────────────────────────────
    _HEAD_SMOOTH = 0.92        # realtime
    _HEAD_SCALE_Z = 12.0       # nghiêng ±12
    _HEAD_SCALE_Y = 12.0       # gật ±12
    _HEAD_SCALE_X = 14.0       # quay ±14

    # ── Body (LLM_Sway) ─────────────────────────────────────────────────
    _BODY_SMOOTH = 0.92        # realtime
    _BODY_SCALE = 14.0         # range ±14

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

        nx = (cursor_x / self._screen_w) * 2 - 1          # -1 .. 1
        ny = (cursor_y / self._screen_h) * 2 - 1          # -1 .. 1 (top = -1)

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


_POSE_CYCLE_MIN = 70
_POSE_CYCLE_MAX = 130
_POSE_JITTER = (-20, 40)


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
    """Triggers a random body pose every ~90 s when idle (not speaking)."""

    def __init__(self, random_source: object | None = None) -> None:
        self.random_source = random_source or random
        self._next_at: float | None = None
        self._last_pose_code: str | None = None

    def reset(self) -> None:
        self._next_at = None
        self._last_pose_code = None

    def update(self, now: float, speaking: bool) -> bool:
        if speaking:
            return False
        if self._next_at is None:
            self._next_at = now + self._roll_interval()
            return False
        if now < self._next_at:
            return False
        self._next_at = now + self._roll_interval()
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

    def _roll_interval(self) -> float:
        return float(
            self.random_source.uniform(_POSE_CYCLE_MIN, _POSE_CYCLE_MAX)
        )
