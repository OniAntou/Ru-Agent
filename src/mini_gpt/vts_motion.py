"""Pure motion policy helpers for the Ru VTube Studio integration."""

from __future__ import annotations

import math
import random
from typing import Iterable


IDLE_SWAY_AMPLITUDE = 5.0
IDLE_SWAY_SPEED = 1.5
SPEAKING_SWAY_AMPLITUDE = 14.0
SPEAKING_SWAY_SPEED = 1.35
MICRO_ACTION_MIN_DELAY = 12.0
MICRO_ACTION_MAX_DELAY = 25.0


def sway_value(elapsed: float, speaking: bool) -> float:
    """Return a bounded, layered sway target for the current state."""

    amplitude = SPEAKING_SWAY_AMPLITUDE if speaking else IDLE_SWAY_AMPLITUDE
    time_value = float(elapsed)
    layered = (
        0.64 * math.sin(time_value * 1.05)
        + 0.24 * math.sin(time_value * 0.47 + 1.7)
        + 0.12 * math.sin(time_value * 2.2 + 0.4)
    )
    envelope = 0.62 + 0.38 * (0.5 + 0.5 * math.sin(time_value * 0.17 + 0.8))
    return amplitude * layered * envelope


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
    """Schedule at most one ambient micro-action per speaking interval."""

    def __init__(self, random_source: object | None = None) -> None:
        self.random_source = random_source or random
        self.next_due_at: float | None = None

    def reset(self) -> None:
        self.next_due_at = None

    def update(self, now: float, speaking: bool) -> bool:
        """Return whether an action is due, resetting during idle periods."""

        if not speaking:
            self.reset()
            return False

        if self.next_due_at is None:
            self.next_due_at = now + self._next_delay()
            return False

        if now < self.next_due_at:
            return False

        self.next_due_at = now + self._next_delay()
        return True

    def _next_delay(self) -> float:
        return float(
            self.random_source.uniform(
                MICRO_ACTION_MIN_DELAY,
                MICRO_ACTION_MAX_DELAY,
            )
        )
