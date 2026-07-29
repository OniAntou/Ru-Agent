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
    """Return the bounded sway value for the current speaking state."""

    amplitude = SPEAKING_SWAY_AMPLITUDE if speaking else IDLE_SWAY_AMPLITUDE
    speed = SPEAKING_SWAY_SPEED if speaking else IDLE_SWAY_SPEED
    return math.sin(elapsed * speed) * amplitude


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
